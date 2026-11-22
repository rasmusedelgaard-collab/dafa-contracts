using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>E-sign settings (AppSettings key "esign"), with defaults. Provider None = e-signing is off.</summary>
public static class ESignSettingsStore
{
    public const string Key = "esign";
    public static readonly string[] Orders = { "Sequential", "Parallel" };

    public static async Task<ESignSettingsDto> LoadAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var settings = new ESignSettingsDto();
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value))
        {
            return settings;
        }
        try
        {
            var stored = JsonSerializer.Deserialize<ESignSettingsDto>(row.Value, JsonDefaults.Options);
            if (stored is null)
            {
                return settings;
            }
            if (SignatureProviderNames.All.Contains(stored.Provider)) settings.Provider = stored.Provider;
            if (stored.DefaultMessage is not null) settings.DefaultMessage = stored.DefaultMessage;
            if (Orders.Contains(stored.DefaultOrder)) settings.DefaultOrder = stored.DefaultOrder;
            if (stored.ReminderDays is not null) settings.ReminderDays = stored.ReminderDays.Where(d => d >= 1 && d <= 60).Distinct().OrderBy(d => d).Take(5).ToList();
            if (stored.ExpiryDays >= 1 && stored.ExpiryDays <= 180) settings.ExpiryDays = stored.ExpiryDays;
            settings.RequireApproved = stored.RequireApproved;
            settings.RestrictToAuthorised = stored.RestrictToAuthorised;
            if (stored.AuthorisedSignatories is not null) settings.AuthorisedSignatories = stored.AuthorisedSignatories.Where(a => a is not null).ToList();
        }
        catch (JsonException)
        {
            // A damaged document falls back to the defaults (e-signing off).
        }
        return settings;
    }
}

/// <summary>Who may send a contract for e-signature (CONTRACT_V4 section 8.1). Pure rules.</summary>
public static class SignatureRules
{
    /// <summary>
    /// The signing permission itself: the user has it and it covers the entity. Administrators do
    /// NOT get it implicitly.
    /// </summary>
    public static bool HasPermissionFor(AccessProfile profile, Guid entityId) =>
        profile.CanSendForSignature && (profile.SignatureEntityIds.Count == 0 || profile.SignatureEntityIds.Contains(entityId));

    public static bool StatusAllowsSending(ContractStatus status, bool requireApproved)
    {
        if (status == ContractStatus.Approved)
        {
            return true;
        }
        return !requireApproved && (status == ContractStatus.Draft || status == ContractStatus.Active);
    }

    /// <summary>
    /// Null when the user may send this contract for signature; otherwise the sentence to show.
    /// The caller has already established that the user can see the contract.
    /// </summary>
    public static string? SendBlocker(AccessProfile profile, Contract contract, ESignSettingsDto settings, string entityName)
    {
        if (settings.Provider == SignatureProviderNames.None)
        {
            return "E-signing is turned off.";
        }
        if (!profile.CanSendForSignature)
        {
            return "You do not have the right to send contracts for signature. Ask an administrator, or a colleague with signing rights.";
        }
        if (!HasPermissionFor(profile, contract.EntityId))
        {
            return "Your signing right does not cover " + (string.IsNullOrWhiteSpace(entityName) ? "this legal entity" : entityName) + ".";
        }
        if (!StatusAllowsSending(contract.Status, settings.RequireApproved))
        {
            return settings.RequireApproved
                ? "The contract must be approved before it can be sent for signature."
                : "A contract can be sent for signature while it is a draft, approved or active.";
        }
        return null;
    }

    public static bool IsActive(SignatureRequestStatus status) =>
        status == SignatureRequestStatus.Draft || status == SignatureRequestStatus.Sent || status == SignatureRequestStatus.PartiallySigned;
}

/// <summary>Maps signature rows to the API shapes.</summary>
public static class SignatureMapper
{
    public static SignerDto ToDto(Signatory signatory, SignatureRequestStatus requestStatus)
    {
        var status = signatory.Status.ToString();
        DateTime? at = signatory.Status switch
        {
            SignatoryStatus.Signed => signatory.SignedAt,
            SignatoryStatus.Declined => signatory.DeclinedAt,
            SignatoryStatus.Viewed => signatory.ViewedAt,
            SignatoryStatus.Sent => signatory.SentAt,
            _ => null
        };
        if (requestStatus == SignatureRequestStatus.Expired
            && signatory.Status != SignatoryStatus.Signed && signatory.Status != SignatoryStatus.Declined)
        {
            status = "Expired";
        }
        return new SignerDto
        {
            Id = signatory.Id,
            Kind = signatory.UserId is null ? "External" : "Internal",
            UserId = signatory.UserId,
            Name = signatory.Name,
            Email = signatory.Email,
            Order = signatory.SortOrder,
            Status = status,
            At = at,
            DeclineReason = signatory.DeclineReason
        };
    }

    public static SignatureRequestDto ToDto(SignatureRequest request, IEnumerable<Signatory> signatories)
    {
        var signers = signatories.Where(s => s.RequestId == request.Id).OrderBy(s => s.SortOrder).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new SignatureRequestDto
        {
            Id = request.Id,
            ContractId = request.ContractId,
            // The API has no PartiallySigned: a request some have signed is still Sent (see signedCount).
            Status = request.Status == SignatureRequestStatus.PartiallySigned ? "Sent" : request.Status.ToString(),
            Provider = request.Provider,
            Order = request.OrderMode.ToString(),
            Message = request.Message ?? "",
            DocumentIds = JsonDefaults.ReadList<Guid>(request.DocumentIdsJson),
            CreatedBy = request.CreatedByName,
            CreatedById = request.CreatedById,
            CreatedAt = request.CreatedAt,
            SentAt = request.SentAt,
            CompletedAt = request.CompletedAt,
            ExpiresAt = request.ExpiresAt,
            Signers = signers.Select(s => ToDto(s, request.Status)).ToList(),
            SignedDocumentId = request.SignedDocumentId,
            SignedCount = signers.Count(s => s.Status == SignatoryStatus.Signed),
            StatusMessage = request.StatusMessage
        };
    }

    /// <summary>The open request of a contract, else the last finished one, else null (ContractDetail.signature).</summary>
    public static async Task<SignatureRequestDto?> LatestAsync(IAppDbContext db, Guid contractId, CancellationToken cancellationToken = default)
    {
        var requests = await db.SignatureRequests.AsNoTracking().Where(r => r.ContractId == contractId).ToListAsync(cancellationToken);
        if (requests.Count == 0)
        {
            return null;
        }
        var request = requests.Where(r => SignatureRules.IsActive(r.Status)).OrderByDescending(r => r.CreatedAt).FirstOrDefault()
            ?? requests.OrderByDescending(r => r.CreatedAt).First();
        var requestId = request.Id;
        var signatories = await db.Signatories.AsNoTracking().Where(s => s.RequestId == requestId).ToListAsync(cancellationToken);
        return ToDto(request, signatories);
    }
}

/// <summary>The provider-neutral webhook body (used by the Simulated provider and by tests).</summary>
public static class SignatureWebhook
{
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static SignatureProviderEvent? One(JsonElement element, string? fallbackReference)
    {
        var reference = Text(element, "reference") ?? fallbackReference;
        var kind = Text(element, "event");
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }
        var result = new SignatureProviderEvent
        {
            Reference = reference!.Trim(),
            Kind = kind!.Trim(),
            SignerEmail = Text(element, "signerEmail"),
            Reason = Text(element, "reason")
        };
        if (Guid.TryParse(Text(element, "signerId"), out var signatoryId))
        {
            result.SignatoryId = signatoryId;
        }
        if (DateTime.TryParse(Text(element, "at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
        {
            result.At = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        }
        return result;
    }

    /// <summary>
    /// { "reference": "...", "event": "Viewed|Signed|Declined|Completed|Expired|Cancelled",
    ///   "signerId"?: "...", "signerEmail"?: "...", "at"?: "ISO time", "reason"?: "..." }
    /// or { "reference"?: "...", "events": [ { ... } ] }. Anything else gives no events.
    /// </summary>
    public static IReadOnlyList<SignatureProviderEvent> ParseGeneric(string body)
    {
        var events = new List<SignatureProviderEvent>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return events;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return events;
            }
            if (root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var reference = Text(root, "reference");
                foreach (var item in list.EnumerateArray())
                {
                    var parsed = One(item, reference);
                    if (parsed is not null) events.Add(parsed);
                }
            }
            else
            {
                var parsed = One(root, null);
                if (parsed is not null) events.Add(parsed);
            }
        }
        catch (JsonException)
        {
            // Not JSON: no events.
        }
        return events;
    }
}

public sealed class SignatureSyncResult
{
    public int Checked { get; set; }
    public int Completed { get; set; }
    public int Expired { get; set; }
    public int Reminded { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// E-signature (CONTRACT_V4 section 8): settings, permission, create and send, resend, cancel,
/// simulate, provider webhook events and the polling job. Every contract read goes through
/// IContractAccessService; the webhook and the job act as "System" and never return contract data.
/// </summary>
public class SignatureService
{
    public const string SigningMethod = "E-signature";
    public const int MaxSigners = 20;
    public const long MaxEnvelopeBytes = 25L * 1024 * 1024;

    private static readonly Regex EmailPattern = new(@"^[^@\s<>()\[\],;:""\\]+@[^@\s<>()\[\],;:""\\]+\.[A-Za-z0-9-]{2,}$", RegexOptions.CultureInvariant);

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly DocumentWriter _documents;
    private readonly IDocumentStore _store;
    private readonly NotificationDispatcher _dispatcher;
    private readonly SignatureProviderRegistry _providers;

    public SignatureService(
        IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock, AuditService audit,
        DocumentWriter documents, IDocumentStore store, NotificationDispatcher dispatcher, SignatureProviderRegistry providers)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _audit = audit;
        _documents = documents;
        _store = store;
        _dispatcher = dispatcher;
        _providers = providers;
    }

    public static bool IsValidEmail(string? email)
    {
        var text = (email ?? "").Trim();
        return text.Length >= 6 && text.Length <= 256 && !text.Any(char.IsControl) && EmailPattern.IsMatch(text);
    }

    // ---------- settings ----------

    private string? AccountOf(string providerName) => _providers.Find(providerName)?.Account;

    public async Task<ESignSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        settings.ProviderAccount = AccountOf(settings.Provider);
        return settings;
    }

    public async Task<ESignSettingsDto> UpdateSettingsAsync(ESignSettingsDto input, CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new InputValidationException("provider", "Send the settings.");
        }
        var errors = new Dictionary<string, string[]>();
        var providerName = (input.Provider ?? "").Trim();
        if (!SignatureProviderNames.All.Contains(providerName))
        {
            ContractValidator.Add(errors, "provider", "Use None, Simulated, DocuSign or Penneo.");
        }
        else if (providerName != SignatureProviderNames.None)
        {
            var provider = _providers.Find(providerName);
            if (provider is null || !provider.IsConfigured)
            {
                ContractValidator.Add(errors, "provider", providerName == SignatureProviderNames.Simulated
                    ? "The simulated provider is not allowed on this server (Signing:AllowSimulated)."
                    : providerName + " is not configured on this server (Signing:" + providerName + ":*). Ask IT to add the credentials first.");
            }
        }
        var message = (input.DefaultMessage ?? "").Trim();
        if (message.Length > 2000) ContractValidator.Add(errors, "defaultMessage", "The message can be at most 2000 characters.");
        if (!ESignSettingsStore.Orders.Contains(input.DefaultOrder)) ContractValidator.Add(errors, "defaultOrder", "Use Sequential or Parallel.");
        var reminderDays = (input.ReminderDays ?? new List<int>()).Distinct().OrderBy(d => d).ToList();
        if (reminderDays.Count > 5 || reminderDays.Any(d => d < 1 || d > 60))
            ContractValidator.Add(errors, "reminderDays", "Enter at most five reminder days between 1 and 60.");
        if (input.ExpiryDays < 1 || input.ExpiryDays > 180) ContractValidator.Add(errors, "expiryDays", "Expiry must be between 1 and 180 days.");

        var entityIds = (await _db.LegalEntities.AsNoTracking().Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
        var userIds = (await _db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(cancellationToken)).ToHashSet();
        var authorised = new List<AuthorisedSignatoriesDto>();
        var seenEntities = new HashSet<Guid>();
        foreach (var item in (input.AuthorisedSignatories ?? new List<AuthorisedSignatoriesDto>()).Where(a => a is not null))
        {
            if (!entityIds.Contains(item.EntityId))
            {
                ContractValidator.Add(errors, "authorisedSignatories", "Unknown legal entity.");
                continue;
            }
            if (!seenEntities.Add(item.EntityId))
            {
                ContractValidator.Add(errors, "authorisedSignatories", "A legal entity can be listed only once.");
                continue;
            }
            var users = (item.UserIds ?? new List<Guid>()).Distinct().ToList();
            if (users.Any(u => !userIds.Contains(u)))
                ContractValidator.Add(errors, "authorisedSignatories", "Unknown user.");
            var names = (item.ExternalNames ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Any(n => n.Length > 200))
                ContractValidator.Add(errors, "authorisedSignatories", "A name can be at most 200 characters.");
            if (users.Count > 0 || names.Count > 0)
            {
                authorised.Add(new AuthorisedSignatoriesDto { EntityId = item.EntityId, UserIds = users, ExternalNames = names });
            }
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        var before = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        var stored = new ESignSettingsDto
        {
            Provider = providerName,
            DefaultMessage = message,
            DefaultOrder = input.DefaultOrder,
            ReminderDays = reminderDays,
            ExpiryDays = input.ExpiryDays,
            RequireApproved = input.RequireApproved,
            AuthorisedSignatories = authorised,
            RestrictToAuthorised = input.RestrictToAuthorised,
            ProviderAccount = null
        };
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == ESignSettingsStore.Key, cancellationToken);
        if (row is null)
        {
            row = new AppSetting { Key = ESignSettingsStore.Key };
            _db.AppSettings.Add(row);
        }
        row.Value = JsonDefaults.Write(stored);
        row.UpdatedAt = _clock.UtcNow;
        _audit.Record("settings.esign", "Updated e-signature settings: provider " + before.Provider + " -> " + stored.Provider
            + ", approval required " + (stored.RequireApproved ? "yes" : "no")
            + ", only authorised signatories " + (stored.RestrictToAuthorised ? "yes" : "no"));
        await _db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    /// <summary>The provider chosen in the settings; 400 feature-off when e-signing is off or the provider is unavailable.</summary>
    private ISignatureProvider RequireProvider(ESignSettingsDto settings)
    {
        if (settings.Provider == SignatureProviderNames.None)
        {
            throw new FeatureOffException("E-signing is turned off in the e-signature settings.");
        }
        var provider = _providers.Find(settings.Provider);
        if (provider is null || !provider.IsConfigured)
        {
            throw new FeatureOffException("The e-signature provider " + settings.Provider + " is not available on this server.");
        }
        return provider;
    }

    // ---------- reads ----------

    private async Task<string> EntityNameAsync(Guid entityId, CancellationToken cancellationToken) =>
        await _db.LegalEntities.AsNoTracking().Where(e => e.Id == entityId).Select(e => e.Name).FirstOrDefaultAsync(cancellationToken) ?? "";

    public async Task<List<SignatureRequestDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await _access.GetReadableAsync(contractId, false, cancellationToken);
        var requests = await _db.SignatureRequests.AsNoTracking().Where(r => r.ContractId == contractId).ToListAsync(cancellationToken);
        var signatories = await _db.Signatories.AsNoTracking().Where(s => s.ContractId == contractId).ToListAsync(cancellationToken);
        return requests.OrderByDescending(r => r.CreatedAt).Select(r => SignatureMapper.ToDto(r, signatories)).ToList();
    }

    public async Task<SignatureRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await _db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new NotFoundException("Signature request not found.");
        // Access to a signature request follows access to its contract.
        await _access.GetReadableAsync(request.ContractId, false, cancellationToken);
        return await ToDtoAsync(request, cancellationToken);
    }

    private async Task<SignatureRequestDto> ToDtoAsync(SignatureRequest request, CancellationToken cancellationToken)
    {
        var requestId = request.Id;
        var signatories = await _db.Signatories.AsNoTracking().Where(s => s.RequestId == requestId).ToListAsync(cancellationToken);
        // Rows changed in this unit of work are tracked; prefer them over the stored copies.
        var local = _db.Signatories.Local.Where(s => s.RequestId == requestId).ToList();
        if (local.Count > 0)
        {
            signatories = local;
        }
        return SignatureMapper.ToDto(request, signatories);
    }

    /// <summary>GET contracts/{id}/signature-permissions: may the caller send it, and who else could.</summary>
    public async Task<SignaturePermissionsDto> GetPermissionsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetReadableAsync(contractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var settings = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        var entityName = await EntityNameAsync(contract.EntityId, cancellationToken);
        var reason = SignatureRules.SendBlocker(profile, contract, settings, entityName);
        if (reason is null)
        {
            var id = contract.Id;
            var open = await _db.SignatureRequests.AsNoTracking().Where(r => r.ContractId == id).Select(r => r.Status).ToListAsync(cancellationToken);
            if (open.Any(SignatureRules.IsActive))
            {
                reason = "A signature request is already open for this contract.";
            }
        }
        var result = new SignaturePermissionsDto { CanSend = reason is null, Reason = reason };

        var candidates = await _db.Users.AsNoTracking().Where(u => u.IsActive && u.CanSendForSignature).ToListAsync(cancellationToken);
        var permitted = new List<AppUser>();
        foreach (var user in candidates.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
        {
            var limit = JsonDefaults.ReadList<Guid>(user.SignatureEntityIdsJson);
            if (limit.Count > 0 && !limit.Contains(contract.EntityId))
            {
                continue;
            }
            // Only colleagues who can see the contract themselves: the list must not reveal more than that.
            if (await ContractAccessService.CanUserReadAsync(_db, user.Id, contract, cancellationToken))
            {
                permitted.Add(user);
            }
            if (permitted.Count >= 50)
            {
                break;
            }
        }
        result.PermittedUsers = await PersonDirectory.ToOptionsAsync(_db, permitted, cancellationToken);
        return result;
    }

    // ---------- create and send ----------

    private async Task<SignatureEnvelope> BuildEnvelopeAsync(
        SignatureRequest request, Contract contract, List<Signatory> signatories, CancellationToken cancellationToken)
    {
        var envelope = new SignatureEnvelope
        {
            RequestId = request.Id,
            ContractId = contract.Id,
            ContractNo = contract.ContractNo,
            Title = contract.Title,
            Message = request.Message ?? "",
            Sequential = request.OrderMode == SignatureOrderMode.Sequential,
            ExpiresAt = request.ExpiresAt
        };
        var documentIds = JsonDefaults.ReadList<Guid>(request.DocumentIdsJson);
        var contractId = contract.Id;
        var documents = await _db.Documents.AsNoTracking().Where(d => d.ContractId == contractId && documentIds.Contains(d.Id)).ToListAsync(cancellationToken);
        long total = 0;
        foreach (var documentId in documentIds)
        {
            var document = documents.FirstOrDefault(d => d.Id == documentId);
            if (document is null)
            {
                continue;
            }
            await using var stream = await _store.OpenReadAsync(document.StoreKey, cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            total += buffer.Length;
            if (total > MaxEnvelopeBytes)
            {
                throw new InputValidationException("documentIds", "The documents are larger than 25 MB together.");
            }
            envelope.Documents.Add(new SignatureEnvelopeDocument { Name = document.Name, Content = buffer.ToArray() });
        }
        foreach (var signatory in signatories.OrderBy(s => s.SortOrder))
        {
            envelope.Signers.Add(new SignatureEnvelopeSigner
            {
                SignatoryId = signatory.Id,
                Name = signatory.Name,
                Email = signatory.Email,
                Order = signatory.SortOrder,
                SignedAt = signatory.SignedAt
            });
        }
        return envelope;
    }

    /// <summary>POST contracts/{id}/signature-requests: validates, sends through the provider and stores the request as Sent.</summary>
    public async Task<SignatureRequestDto> CreateAndSendAsync(Guid contractId, SignatureRequestInput input, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetReadableAsync(contractId, true, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var settings = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        var provider = RequireProvider(settings);
        var entityName = await EntityNameAsync(contract.EntityId, cancellationToken);
        var blocker = SignatureRules.SendBlocker(profile, contract, settings, entityName);
        if (blocker is not null)
        {
            throw new NotPermittedException(blocker);
        }
        input ??= new SignatureRequestInput();
        var errors = new Dictionary<string, string[]>();
        var now = _clock.UtcNow;

        // Documents: at least one, all of this contract, never the system's approval log.
        var documentIds = (input.DocumentIds ?? new List<Guid>()).Distinct().ToList();
        var id = contract.Id;
        var documents = await _db.Documents.AsNoTracking().Where(d => d.ContractId == id && documentIds.Contains(d.Id)).ToListAsync(cancellationToken);
        if (documentIds.Count == 0)
            ContractValidator.Add(errors, "documentIds", "Choose at least one document to sign.");
        else if (documents.Count != documentIds.Count)
            ContractValidator.Add(errors, "documentIds", "A chosen document does not belong to this contract.");
        else if (documents.Any(d => d.Kind == DocumentKind.ApprovalLog))
            ContractValidator.Add(errors, "documentIds", "The approval log cannot be sent for signature.");

        // Order mode and message.
        var orderText = string.IsNullOrWhiteSpace(input.Order) ? settings.DefaultOrder : input.Order!.Trim();
        if (!Enum.TryParse<SignatureOrderMode>(orderText, false, out var orderMode) || !Enum.IsDefined(typeof(SignatureOrderMode), orderMode) || int.TryParse(orderText, out _))
        {
            ContractValidator.Add(errors, "order", "Use Sequential or Parallel.");
            orderMode = SignatureOrderMode.Parallel;
        }
        var message = string.IsNullOrWhiteSpace(input.Message) ? settings.DefaultMessage : input.Message!.Trim();
        if (message.Length > 2000) ContractValidator.Add(errors, "message", "The message can be at most 2000 characters.");

        // Signers.
        var signerInputs = (input.Signers ?? new List<SignerInput>()).Where(s => s is not null).ToList();
        if (signerInputs.Count == 0) ContractValidator.Add(errors, "signers", "Add at least one signer.");
        if (signerInputs.Count > MaxSigners) ContractValidator.Add(errors, "signers", "A request can have at most " + MaxSigners + " signers.");
        var signatories = new List<Signatory>();
        var requestId = Guid.NewGuid();
        var index = 0;
        foreach (var signer in signerInputs.Take(MaxSigners))
        {
            index++;
            var field = "signers[" + (index - 1) + "]";
            var kind = (signer.Kind ?? (signer.UserId is null ? "External" : "Internal")).Trim();
            var order = signer.Order ?? index;
            if (order < 1 || order > 100)
            {
                ContractValidator.Add(errors, field + ".order", "The order must be between 1 and 100.");
                order = index;
            }
            if (kind == "Internal")
            {
                if (signer.UserId is null)
                {
                    ContractValidator.Add(errors, field + ".userId", "Choose the colleague who signs.");
                    continue;
                }
                var userId = signer.UserId.Value;
                var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
                if (user is null || !user.IsActive)
                {
                    ContractValidator.Add(errors, field + ".userId", "Unknown or inactive user.");
                    continue;
                }
                if (!IsValidEmail(user.Email))
                {
                    ContractValidator.Add(errors, field + ".userId", user.Name + " has no valid e-mail address.");
                    continue;
                }
                signatories.Add(new Signatory { Id = Guid.NewGuid(), RequestId = requestId, ContractId = contract.Id, UserId = user.Id, Name = user.Name, Email = user.Email.Trim(), SortOrder = order });
            }
            else if (kind == "External")
            {
                var name = (signer.Name ?? "").Trim();
                var email = (signer.Email ?? "").Trim();
                var ok = true;
                if (name.Length < 1 || name.Length > 200 || name.Any(char.IsControl))
                {
                    ContractValidator.Add(errors, field + ".name", "Enter the signer's name (at most 200 characters).");
                    ok = false;
                }
                if (!IsValidEmail(email))
                {
                    ContractValidator.Add(errors, field + ".email", "Enter a valid e-mail address.");
                    ok = false;
                }
                if (ok)
                {
                    signatories.Add(new Signatory { Id = Guid.NewGuid(), RequestId = requestId, ContractId = contract.Id, UserId = null, Name = name, Email = email, SortOrder = order });
                }
            }
            else
            {
                ContractValidator.Add(errors, field + ".kind", "Use Internal or External.");
            }
        }
        if (signatories.GroupBy(s => s.Email, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            ContractValidator.Add(errors, "signers", "Each signer needs their own e-mail address.");

        if (settings.RestrictToAuthorised && errors.Count == 0)
        {
            var authorised = settings.AuthorisedSignatories.FirstOrDefault(a => a.EntityId == contract.EntityId);
            var hasAuthorised = authorised is not null && signatories.Any(s =>
                (s.UserId is not null && authorised.UserIds.Contains(s.UserId.Value))
                || (s.UserId is null && authorised.ExternalNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase)));
            if (!hasAuthorised)
                ContractValidator.Add(errors, "signers", "At least one signer must be an authorised signatory of " + (entityName.Length == 0 ? "the legal entity" : entityName) + ".");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        // One open request per contract (also guaranteed by a filtered unique index).
        var existing = await _db.SignatureRequests.AsNoTracking().Where(r => r.ContractId == id).Select(r => r.Status).ToListAsync(cancellationToken);
        if (existing.Any(SignatureRules.IsActive))
        {
            throw new ConflictException("A signature request is already open for this contract. Cancel it before sending a new one.", ProblemTypesV4.SignatureState);
        }

        var request = new SignatureRequest
        {
            Id = requestId,
            ContractId = contract.Id,
            Provider = provider.Name,
            Status = SignatureRequestStatus.Draft,
            OrderMode = orderMode,
            Message = message,
            DocumentIdsJson = JsonDefaults.Write(documentIds),
            CreatedById = profile.UserId,
            CreatedByName = profile.Name,
            CreatedAt = now,
            ExpiresAt = now.AddDays(settings.ExpiryDays)
        };
        var envelope = await BuildEnvelopeAsync(request, contract, signatories, cancellationToken);
        string reference;
        try
        {
            reference = await provider.SendAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
        {
            throw new ExternalCallException("The e-signature provider " + provider.Name + " did not accept the request: " + Short(ex.Message));
        }

        request.ProviderReference = reference;
        request.Status = SignatureRequestStatus.Sent;
        request.SentAt = now;
        InviteNext(request, signatories, now);
        _db.SignatureRequests.Add(request);
        _db.Signatories.AddRange(signatories);
        _audit.Record("signature.sent",
            "Sent for e-signature (" + provider.Name + ", " + orderMode + ") to " + string.Join(", ", signatories.OrderBy(s => s.SortOrder).Select(s => s.Name))
                + "; documents: " + string.Join(", ", documents.Select(d => d.Name)),
            contract.Id);
        await NotifySignersAsync(contract, signatories.Where(s => s.Status == SignatoryStatus.Sent).ToList(), message, cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request was sent at the same moment (unique index): take ours back at the provider.
            _db.DiscardChanges();
            try
            {
                await provider.CancelAsync(reference, "Superseded", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort; the envelope expires on its own.
            }
            throw new ConflictException("A signature request is already open for this contract.", ProblemTypesV4.SignatureState);
        }
        return SignatureMapper.ToDto(request, signatories);
    }

    private static string Short(string? text)
    {
        var value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 300 ? value.Substring(0, 300) : value;
    }

    /// <summary>
    /// Invites whoever is next: everyone who is waiting (Parallel), or the waiting signers with the
    /// lowest order once nobody with a lower order is still signing (Sequential). Returns the newly invited.
    /// </summary>
    private static List<Signatory> InviteNext(SignatureRequest request, List<Signatory> signatories, DateTime now)
    {
        var waiting = signatories.Where(s => s.Status == SignatoryStatus.Waiting).ToList();
        if (waiting.Count == 0)
        {
            return waiting;
        }
        List<Signatory> next;
        if (request.OrderMode == SignatureOrderMode.Parallel)
        {
            next = waiting;
        }
        else
        {
            if (signatories.Any(s => s.Status == SignatoryStatus.Sent || s.Status == SignatoryStatus.Viewed))
            {
                return new List<Signatory>();
            }
            var lowest = waiting.Min(s => s.SortOrder);
            next = waiting.Where(s => s.SortOrder == lowest).ToList();
        }
        foreach (var signatory in next)
        {
            signatory.Status = SignatoryStatus.Sent;
            signatory.SentAt = now;
        }
        return next;
    }

    private async Task NotifySignersAsync(Contract contract, List<Signatory> invited, string? message, CancellationToken cancellationToken)
    {
        foreach (var signatory in invited.Where(s => s.UserId is not null))
        {
            var data = new Dictionary<string, string>
            {
                ["signer.name"] = signatory.Name,
                ["comment"] = message ?? "",
                ["step.role"] = "Signer"
            };
            await _dispatcher.DispatchAsync(
                NotificationCatalog.SignatureRequested, NotificationCatalog.SignatureRequested, contract.Id,
                new[] { signatory.UserId!.Value },
                "Please sign: " + contract.Title,
                "You are asked to sign contract " + contract.ContractNo + " (" + contract.Title + "). The signing link comes from the e-signature provider."
                    + (string.IsNullOrWhiteSpace(message) ? "" : " " + message),
                data, cancellationToken);
        }
    }

    /// <summary>Owner, delegate and the sender, once each.</summary>
    private static List<Guid> Stakeholders(Contract contract, SignatureRequest request)
    {
        var result = new List<Guid> { contract.OwnerId };
        if (contract.DelegateId is not null && !result.Contains(contract.DelegateId.Value)) result.Add(contract.DelegateId.Value);
        if (!result.Contains(request.CreatedById)) result.Add(request.CreatedById);
        return result;
    }

    // ---------- resend, cancel, simulate ----------

    /// <summary>The sender, anyone with the signing permission for the contract's entity, or an administrator.</summary>
    private async Task<(SignatureRequest Request, Contract Contract, List<Signatory> Signatories)> LoadManageableAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _db.SignatureRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new NotFoundException("Signature request not found.");
        var contract = await _access.GetReadableAsync(request.ContractId, true, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var allowed = profile.IsAdmin || request.CreatedById == profile.UserId || SignatureRules.HasPermissionFor(profile, contract.EntityId);
        if (!allowed)
        {
            throw new NotPermittedException("Only the sender, a colleague with signing rights for this legal entity or an administrator can change a signature request.");
        }
        var signatories = await _db.Signatories.Where(s => s.RequestId == requestId).ToListAsync(cancellationToken);
        return (request, contract, signatories);
    }

    private static void RequireOpen(SignatureRequest request)
    {
        if (request.Status != SignatureRequestStatus.Sent && request.Status != SignatureRequestStatus.PartiallySigned)
        {
            throw new ConflictException("The signature request is " + request.Status + " and can no longer be changed.", ProblemTypesV4.SignatureState);
        }
    }

    public async Task<SignatureRequestDto> ResendAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var (request, contract, signatories) = await LoadManageableAsync(requestId, cancellationToken);
        RequireOpen(request);
        var pending = signatories.Where(s => s.Status == SignatoryStatus.Sent || s.Status == SignatoryStatus.Viewed).ToList();
        await RemindAsync(request, contract, pending, cancellationToken);
        _audit.Record("signature.resent", "Reminded " + string.Join(", ", pending.Select(s => s.Name)) + " to sign", contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return SignatureMapper.ToDto(request, signatories);
    }

    private async Task RemindAsync(SignatureRequest request, Contract contract, List<Signatory> pending, CancellationToken cancellationToken)
    {
        var provider = _providers.Find(request.Provider);
        if (provider is not null && provider.IsConfigured && !string.IsNullOrEmpty(request.ProviderReference))
        {
            try
            {
                await provider.RemindAsync(request.ProviderReference!, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
            {
                throw new ExternalCallException("The e-signature provider " + provider.Name + " could not send the reminder: " + Short(ex.Message));
            }
        }
        await NotifySignersAsync(contract, pending, "Reminder: your signature is still missing.", cancellationToken);
        request.LastReminderAt = _clock.UtcNow;
    }

    public async Task<SignatureRequestDto> CancelAsync(Guid requestId, string? reason, CancellationToken cancellationToken = default)
    {
        var (request, contract, signatories) = await LoadManageableAsync(requestId, cancellationToken);
        RequireOpen(request);
        var text = string.IsNullOrWhiteSpace(reason) ? "Cancelled by " + (_currentUser.UserName ?? "the sender") : Short(reason);
        var provider = _providers.Find(request.Provider);
        if (provider is not null && provider.IsConfigured && !string.IsNullOrEmpty(request.ProviderReference))
        {
            try
            {
                await provider.CancelAsync(request.ProviderReference!, text, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
            {
                throw new ExternalCallException("The e-signature provider " + provider.Name + " could not cancel the request: " + Short(ex.Message));
            }
        }
        request.Status = SignatureRequestStatus.Cancelled;
        request.CompletedAt = _clock.UtcNow;
        request.StatusMessage = text;
        _audit.Record("signature.cancelled", "Cancelled the signature request" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + text), contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return SignatureMapper.ToDto(request, signatories);
    }

    /// <summary>POST signature-requests/{id}/simulate: only while the Simulated provider is the chosen one.</summary>
    public async Task<SignatureRequestDto> SimulateAsync(Guid requestId, SimulateSignatureInput input, CancellationToken cancellationToken = default)
    {
        var settings = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        var simulated = _providers.Find(SignatureProviderNames.Simulated);
        if (settings.Provider != SignatureProviderNames.Simulated || simulated is null || !simulated.IsConfigured)
        {
            throw new FeatureOffException("Signatures can only be simulated while the Simulated e-signature provider is selected.");
        }
        var (request, contract, signatories) = await LoadManageableAsync(requestId, cancellationToken);
        if (request.Provider != SignatureProviderNames.Simulated)
        {
            throw new FeatureOffException("This request was sent with " + request.Provider + " and cannot be simulated.");
        }
        RequireOpen(request);
        if (input?.SignerId is null)
        {
            throw new InputValidationException("signerId", "Choose the signer.");
        }
        var signatory = signatories.FirstOrDefault(s => s.Id == input.SignerId.Value)
            ?? throw new InputValidationException("signerId", "The signer does not belong to this request.");
        string kind;
        switch ((input.Action ?? "").Trim())
        {
            case "View": kind = SignatureProviderEvent.Viewed; break;
            case "Sign": kind = SignatureProviderEvent.Signed; break;
            case "Decline": kind = SignatureProviderEvent.Declined; break;
            default: throw new InputValidationException("action", "Use View, Sign or Decline.");
        }
        if (signatory.Status != SignatoryStatus.Sent && signatory.Status != SignatoryStatus.Viewed)
        {
            throw new ConflictException(
                signatory.Status == SignatoryStatus.Waiting
                    ? "It is not " + signatory.Name + "'s turn to sign yet."
                    : signatory.Name + " has already " + (signatory.Status == SignatoryStatus.Signed ? "signed" : "declined") + ".",
                ProblemTypesV4.SignatureState);
        }
        await ApplyEventAsync(request, contract, signatories, new SignatureProviderEvent
        {
            Reference = request.ProviderReference ?? "",
            Kind = kind,
            SignatoryId = signatory.Id,
            At = _clock.UtcNow,
            Reason = input.Reason
        }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return SignatureMapper.ToDto(request, signatories);
    }

    // ---------- events (simulate, webhook, polling) ----------

    /// <summary>
    /// Applies one provider event to tracked rows; the caller saves. Events about a request that is
    /// no longer open, or about a signer who already signed or declined, are ignored (returns false),
    /// so a webhook that arrives twice changes nothing.
    /// </summary>
    private async Task<bool> ApplyEventAsync(
        SignatureRequest request, Contract contract, List<Signatory> signatories, SignatureProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        if (request.Status != SignatureRequestStatus.Sent && request.Status != SignatureRequestStatus.PartiallySigned)
        {
            return false;
        }
        var at = providerEvent.At ?? _clock.UtcNow;
        Signatory? signatory = null;
        if (providerEvent.SignatoryId is not null)
        {
            signatory = signatories.FirstOrDefault(s => s.Id == providerEvent.SignatoryId.Value);
        }
        if (signatory is null && !string.IsNullOrWhiteSpace(providerEvent.SignerEmail))
        {
            signatory = signatories.FirstOrDefault(s => string.Equals(s.Email, providerEvent.SignerEmail.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        var open = signatory is not null && (signatory.Status == SignatoryStatus.Sent || signatory.Status == SignatoryStatus.Viewed);

        switch (providerEvent.Kind)
        {
            case SignatureProviderEvent.Viewed:
                if (signatory is null || signatory.Status != SignatoryStatus.Sent)
                {
                    return false;
                }
                signatory.Status = SignatoryStatus.Viewed;
                signatory.ViewedAt = at;
                _audit.Record("signature.viewed", signatory.Name + " opened the document for signing", contract.Id);
                return true;

            case SignatureProviderEvent.Signed:
                if (signatory is null || !open)
                {
                    return false;
                }
                signatory.Status = SignatoryStatus.Signed;
                signatory.SignedAt = at;
                _audit.Record("signature.signed", signatory.Name + " signed", contract.Id);
                if (signatories.All(s => s.Status == SignatoryStatus.Signed))
                {
                    await CompleteAsync(request, contract, signatories, at, cancellationToken);
                }
                else
                {
                    request.Status = SignatureRequestStatus.PartiallySigned;
                    var invited = InviteNext(request, signatories, _clock.UtcNow);
                    await NotifySignersAsync(contract, invited, request.Message, cancellationToken);
                }
                return true;

            case SignatureProviderEvent.Declined:
            {
                if (signatory is not null && open)
                {
                    signatory.Status = SignatoryStatus.Declined;
                    signatory.DeclinedAt = at;
                    signatory.DeclineReason = string.IsNullOrWhiteSpace(providerEvent.Reason) ? null : Short(providerEvent.Reason);
                }
                else if (signatory is not null)
                {
                    return false;
                }
                var who = signatory?.Name ?? "A signer";
                var reason = string.IsNullOrWhiteSpace(providerEvent.Reason) ? "" : ": " + Short(providerEvent.Reason);
                request.Status = SignatureRequestStatus.Declined;
                request.CompletedAt = at;
                request.StatusMessage = who + " declined" + reason;
                _audit.Record("signature.declined", who + " declined to sign" + reason + ". The contract's status is unchanged.", contract.Id);
                await NotifyNotCompletedAsync(request, contract, who + " declined to sign" + reason + ".", who, cancellationToken);
                return true;
            }

            case SignatureProviderEvent.Completed:
                foreach (var pending in signatories.Where(s => s.Status != SignatoryStatus.Signed))
                {
                    pending.Status = SignatoryStatus.Signed;
                    pending.SignedAt = at;
                }
                await CompleteAsync(request, contract, signatories, at, cancellationToken);
                return true;

            case SignatureProviderEvent.Expired:
                request.Status = SignatureRequestStatus.Expired;
                request.CompletedAt = at;
                request.StatusMessage = "Expired before everyone had signed";
                _audit.Record("signature.expired", "The signature request expired before everyone had signed. The contract's status is unchanged.", contract.Id);
                await NotifyNotCompletedAsync(request, contract, "The signature request expired before everyone had signed.", "", cancellationToken);
                return true;

            case SignatureProviderEvent.Cancelled:
                request.Status = SignatureRequestStatus.Cancelled;
                request.CompletedAt = at;
                request.StatusMessage = string.IsNullOrWhiteSpace(providerEvent.Reason) ? "Cancelled at the e-signature provider" : Short(providerEvent.Reason);
                _audit.Record("signature.cancelled", "The signature request was cancelled at the e-signature provider", contract.Id);
                return true;
        }
        return false;
    }

    private async Task NotifyNotCompletedAsync(SignatureRequest request, Contract contract, string text, string signerName, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>
        {
            ["signer.name"] = signerName,
            ["comment"] = text,
            ["step.role"] = "Signer"
        };
        await _dispatcher.DispatchAsync(
            NotificationCatalog.SignatureDeclined, NotificationCatalog.SignatureDeclined, contract.Id, Stakeholders(contract, request),
            "Signature not completed: " + contract.Title,
            "Contract " + contract.ContractNo + " (" + contract.Title + "): " + text + " Send a new request when the matter is settled.",
            data, cancellationToken);
    }

    /// <summary>The name of the signed copy: "&lt;original&gt; – signed.pdf".</summary>
    public static string SignedFileName(string? originalName)
    {
        var name = Path.GetFileNameWithoutExtension(DocumentWriter.SafeFileName(originalName));
        if (name.Length > 100)
        {
            name = name.Substring(0, 100);
        }
        return name + " \u2013 signed.pdf";
    }

    /// <summary>
    /// Everyone has signed: stores the signed file as a new Main document, records the signature on
    /// the contract and, when the contract was Approved, makes it Active (as the manual "record
    /// signature" does). When the provider cannot deliver the signed file yet, the request stays
    /// open and the polling job tries again.
    /// </summary>
    private async Task CompleteAsync(SignatureRequest request, Contract contract, List<Signatory> signatories, DateTime at, CancellationToken cancellationToken)
    {
        var provider = _providers.Find(request.Provider);
        SignedFile signed;
        try
        {
            if (provider is null)
            {
                throw new InvalidOperationException("The provider " + request.Provider + " is not registered.");
            }
            var envelope = await BuildEnvelopeAsync(request, contract, signatories, cancellationToken);
            signed = await provider.GetSignedDocumentAsync(envelope, request.ProviderReference ?? "", cancellationToken);
            if (signed.Content.Length == 0)
            {
                throw new InvalidOperationException("The provider returned an empty file.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Status = SignatureRequestStatus.PartiallySigned;
            request.StatusMessage = "Everyone has signed; the signed document could not be fetched yet (" + Short(ex.Message) + ").";
            return;
        }

        var firstDocumentId = JsonDefaults.ReadList<Guid>(request.DocumentIdsJson).FirstOrDefault();
        var originalName = await _db.Documents.AsNoTracking().Where(d => d.Id == firstDocumentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);
        var document = await _documents.AddAsync(contract, SignedFileName(originalName ?? contract.ContractNo), DocumentKind.Main, signed.Content, null, cancellationToken);

        request.Status = SignatureRequestStatus.Completed;
        request.CompletedAt = at;
        request.SignedDocumentId = document.Id;
        request.StatusMessage = null;

        var wasApproved = contract.Status == ContractStatus.Approved;
        contract.SignedDate = _clock.Today;
        contract.SigningMethod = SigningMethod;
        contract.UpdatedAt = _clock.UtcNow;
        if (wasApproved)
        {
            contract.Status = ContractStatus.Active;
        }
        _audit.Record("signature.completed",
            "Signed by everyone (" + string.Join(", ", signatories.OrderBy(s => s.SortOrder).Select(s => s.Name)) + "). Signed document saved as "
                + document.Name + " (v" + document.Version + ").",
            contract.Id);
        if (wasApproved)
        {
            _audit.Record("contract.signed", "Signed by e-signature (" + request.Provider + "); contract is active", contract.Id);
        }

        var data = new Dictionary<string, string>
        {
            ["signer.name"] = string.Join(", ", signatories.OrderBy(s => s.SortOrder).Select(s => s.Name)),
            ["comment"] = "",
            ["step.role"] = "Signer"
        };
        await _dispatcher.DispatchAsync(
            NotificationCatalog.SignatureCompleted, NotificationCatalog.SignatureCompleted, contract.Id, Stakeholders(contract, request),
            "Signed: " + contract.Title,
            "Contract " + contract.ContractNo + " (" + contract.Title + ") has been signed by everyone. The signed document is in the contract's documents."
                + (wasApproved ? " The contract is now active." : ""),
            data, cancellationToken);
    }

    /// <summary>
    /// A provider webhook (the caller has validated the shared secret). Returns how many events
    /// changed something. Unknown references are ignored: the answer never tells whether a
    /// reference exists.
    /// </summary>
    public async Task<int> HandleWebhookAsync(string providerName, string body, CancellationToken cancellationToken = default)
    {
        var provider = _providers.Find(providerName) ?? throw new NotFoundException("Unknown e-signature provider.");
        var applied = 0;
        foreach (var providerEvent in provider.ParseWebhook(body))
        {
            var reference = providerEvent.Reference;
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }
            var name = provider.Name;
            var request = await _db.SignatureRequests.FirstOrDefaultAsync(r => r.ProviderReference == reference && r.Provider == name, cancellationToken);
            if (request is null)
            {
                continue;
            }
            var contractId = request.ContractId;
            var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
            if (contract is null)
            {
                continue;
            }
            var requestId = request.Id;
            var signatories = await _db.Signatories.Where(s => s.RequestId == requestId).ToListAsync(cancellationToken);
            if (await ApplyEventAsync(request, contract, signatories, providerEvent, cancellationToken))
            {
                applied++;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
        return applied;
    }

    /// <summary>
    /// Job SignatureSync: asks the provider about every open request, finishes requests everyone
    /// has signed, expires requests past their expiry date and sends the reminders of the settings.
    /// One failing request never stops the others.
    /// </summary>
    public async Task<SignatureSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var result = new SignatureSyncResult();
        var settings = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        var ids = (await _db.SignatureRequests.AsNoTracking()
                .Where(r => r.Status == SignatureRequestStatus.Sent || r.Status == SignatureRequestStatus.PartiallySigned)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken));
        foreach (var requestId in ids)
        {
            try
            {
                var request = await _db.SignatureRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
                if (request is null)
                {
                    continue;
                }
                var contractId = request.ContractId;
                var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
                if (contract is null)
                {
                    continue;
                }
                var signatories = await _db.Signatories.Where(s => s.RequestId == requestId).ToListAsync(cancellationToken);
                var now = _clock.UtcNow;
                result.Checked++;
                request.LastPolledAt = now;
                var provider = _providers.Find(request.Provider);

                if (provider is not null && provider.IsConfigured && !string.IsNullOrEmpty(request.ProviderReference))
                {
                    foreach (var providerEvent in await provider.GetEventsAsync(request.ProviderReference!, cancellationToken))
                    {
                        await ApplyEventAsync(request, contract, signatories, providerEvent, cancellationToken);
                    }
                }
                var stillOpen = request.Status == SignatureRequestStatus.Sent || request.Status == SignatureRequestStatus.PartiallySigned;
                if (stillOpen && signatories.Count > 0 && signatories.All(s => s.Status == SignatoryStatus.Signed))
                {
                    // Everyone signed earlier but the signed file could not be fetched then.
                    await CompleteAsync(request, contract, signatories, now, cancellationToken);
                    stillOpen = request.Status != SignatureRequestStatus.Completed;
                }
                if (request.Status == SignatureRequestStatus.Completed)
                {
                    result.Completed++;
                }
                if (stillOpen && request.ExpiresAt is not null && request.ExpiresAt.Value <= now)
                {
                    if (provider is not null && provider.IsConfigured && !string.IsNullOrEmpty(request.ProviderReference))
                    {
                        try
                        {
                            await provider.CancelAsync(request.ProviderReference!, "Expired", cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // The request is expired here whatever the provider answers.
                        }
                    }
                    await ApplyEventAsync(request, contract, signatories,
                        new SignatureProviderEvent { Reference = request.ProviderReference ?? "", Kind = SignatureProviderEvent.Expired, At = now }, cancellationToken);
                    result.Expired++;
                    stillOpen = false;
                }
                if (stillOpen && request.SentAt is not null)
                {
                    // A reminder is due when one of the reminder days has passed since the last reminder (or the sending).
                    var sentAt = request.SentAt.Value;
                    var due = settings.ReminderDays.Any(day =>
                        sentAt.AddDays(day) <= now && (request.LastReminderAt is null || request.LastReminderAt.Value < sentAt.AddDays(day)));
                    var pending = signatories.Where(s => s.Status == SignatoryStatus.Sent || s.Status == SignatoryStatus.Viewed).ToList();
                    if (due && pending.Count > 0)
                    {
                        await RemindAsync(request, contract, pending, cancellationToken);
                        _audit.Record("signature.resent", "Automatic reminder to " + string.Join(", ", pending.Select(s => s.Name)), contract.Id);
                        result.Reminded++;
                    }
                }
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _db.DiscardChanges();
                result.Errors.Add("Request " + requestId + ": " + Short(ex.Message));
            }
        }
        return result;
    }

    /// <summary>A hex SHA-256, for the signature page of the Simulated provider.</summary>
    public static string Sha256Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
