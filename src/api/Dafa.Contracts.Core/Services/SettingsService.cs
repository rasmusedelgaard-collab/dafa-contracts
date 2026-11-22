using System.Text.Json;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Problem types added in release 3 (CONTRACT_V3 section 0.6).</summary>
public static class ProblemTypesV3
{
    public const string InUse = "urn:dafa:problem:in-use";
    public const string SyncedField = "urn:dafa:problem:synced-field";
    public const string ImportState = "urn:dafa:problem:import-state";
    public const string FeatureOff = "urn:dafa:problem:feature-off";
}

/// <summary>A 400 with problem type feature-off.</summary>
public class FeatureOffException : AppException
{
    public FeatureOffException(string message) : base(400, "Feature turned off", message)
    {
        ProblemType = ProblemTypesV3.FeatureOff;
    }
}

/// <summary>General settings (AppSettings key "general"), with defaults.</summary>
public static class AppSettingsStore
{
    public const string Key = "general";
    public const string DigestDefaultKey = "notifications.digestDefault";

    public static readonly string[] DateFormats = { "d MMM yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "MM/dd/yyyy" };
    public static readonly string[] NumberFormats = { "en-GB", "da-DK", "de-DE", "en-US" };
    public static readonly string[] LandingPages = { "home", "contracts", "renewals", "approvals", "overview" };
    public static readonly string[] Roles = { "Reader", "ContractOwner", "Approver", "Admin" };

    public static AppSettingsDto Defaults()
    {
        var settings = new AppSettingsDto
        {
            DataQualityFields = DataQuality.Defaults.ToList(),
            CounterpartyCreation = "AdminOnly"
        };
        foreach (var role in Roles)
        {
            settings.LandingPageByRole[role] = "home";
        }
        return settings;
    }

    public static async Task<AppSettingsDto> LoadAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var settings = await LoadStoredAsync(db, cancellationToken);
        // Release 4: features.eSign mirrors the e-sign settings (a provider other than None is chosen).
        settings.Features.ESign = (await ESignSettingsStore.LoadAsync(db, cancellationToken)).Provider != SignatureProviderNames.None;
        return settings;
    }

    private static async Task<AppSettingsDto> LoadStoredAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);
        var settings = Defaults();
        if (row is null || string.IsNullOrWhiteSpace(row.Value))
        {
            return settings;
        }
        try
        {
            var stored = JsonSerializer.Deserialize<AppSettingsDto>(row.Value, JsonDefaults.Options);
            if (stored is null)
            {
                return settings;
            }
            if (!string.IsNullOrWhiteSpace(stored.AppName)) settings.AppName = stored.AppName;
            if (DateFormats.Contains(stored.DateFormat)) settings.DateFormat = stored.DateFormat;
            if (NumberFormats.Contains(stored.NumberFormat)) settings.NumberFormat = stored.NumberFormat;
            foreach (var pair in stored.LandingPageByRole ?? new Dictionary<string, string>())
            {
                if (Roles.Contains(pair.Key) && LandingPages.Contains(pair.Value))
                {
                    settings.LandingPageByRole[pair.Key] = pair.Value;
                }
            }
            if (stored.Features is not null) settings.Features = stored.Features;
            if (stored.SessionTimeoutMinutes >= 15 && stored.SessionTimeoutMinutes <= 1440) settings.SessionTimeoutMinutes = stored.SessionTimeoutMinutes;
            if (stored.DataQualityFields is not null) settings.DataQualityFields = stored.DataQualityFields.Distinct().ToList();
            if (stored.DdThresholdDkk >= 0) settings.DdThresholdDkk = stored.DdThresholdDkk;
            settings.CounterpartyCreation = NormalizeCounterpartyCreation(stored.CounterpartyCreation) ?? settings.CounterpartyCreation;
        }
        catch (JsonException)
        {
            // A damaged document falls back to the defaults.
        }
        return settings;
    }

    public static readonly string[] CounterpartyCreationValues = { "AdminOnly", "ContractOwners", "AllUsers" };

    /// <summary>
    /// The stored name of a counterpartyCreation value, or null when unknown. The names of the
    /// release 4 brief (Admins, Everyone) are accepted as aliases.
    /// </summary>
    public static string? NormalizeCounterpartyCreation(string? value)
    {
        var text = (value ?? "").Trim();
        if (text == "Admins") return "AdminOnly";
        if (text == "Everyone") return "AllUsers";
        return CounterpartyCreationValues.Contains(text) ? text : null;
    }

    /// <summary>Release 4: whether a user may create counterparties from the contract form (POST counterparties).</summary>
    public static bool MayCreateCounterparty(AppSettingsDto settings, AccessProfile profile)
    {
        if (profile.IsAdmin)
        {
            return true;
        }
        switch (settings.CounterpartyCreation)
        {
            case "AllUsers": return true;
            case "ContractOwners": return profile.HasRole(AppRole.ContractOwner);
            default: return false;
        }
    }

    public static async Task<string> LoadDigestDefaultAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == DigestDefaultKey, cancellationToken);
        var value = row?.Value;
        return value == "Off" || value == "Daily" || value == "Weekly" ? value! : "Weekly";
    }

    /// <summary>Landing page for a user: preference, else the role default (Admin, Approver, ContractOwner, Reader order).</summary>
    public static string ResolveLandingPage(AppSettingsDto settings, AccessProfile profile, string? preference)
    {
        if (!string.IsNullOrWhiteSpace(preference) && LandingPages.Contains(preference))
        {
            return preference!;
        }
        foreach (var role in new[] { AppRole.Admin, AppRole.Approver, AppRole.ContractOwner, AppRole.Reader })
        {
            if (profile.HasRole(role) && settings.LandingPageByRole.TryGetValue(role.ToString(), out var page))
            {
                return page;
            }
        }
        return "home";
    }
}

/// <summary>A notification event: label, default channels/recipients/texts and its placeholders.</summary>
public sealed record NotificationEventInfo(
    string Event, string Label, string NaturalRecipient, string[] Channels, string[] Recipients,
    string Subject, string Body, string[] Placeholders);

/// <summary>The notification events of CONTRACT_V3 section 9.6.</summary>
public static class NotificationCatalog
{
    public const string NoticeReminder = "NoticeReminder";
    public const string Escalation = "Escalation";
    public const string ObligationDue = "ObligationDue";
    public const string AnnualReview = "AnnualReview";
    public const string ApprovalRequested = "ApprovalRequested";
    public const string ApprovalDecided = "ApprovalDecided";
    public const string ApprovalDelegated = "ApprovalDelegated";
    public const string RenewalDecision = "RenewalDecision";
    public const string Mention = "Mention";
    public const string WatchedContractChanged = "WatchedContractChanged";
    public const string DataCompletionRequested = "DataCompletionRequested";
    public const string ImportFinished = "ImportFinished";
    public const string SyncFailed = "SyncFailed";
    // Release 4 (CONTRACT_V4 section 8)
    public const string SignatureRequested = "SignatureRequested";
    public const string SignatureCompleted = "SignatureCompleted";
    public const string SignatureDeclined = "SignatureDeclined";

    public static readonly string[] ChannelNames = { "Email", "Teams", "InApp" };
    public static readonly string[] RecipientRules = { "ReminderRecipient", "Owner", "Delegate", "Responsible", "Watchers", "Approver", "Mentioned", "Admins" };

    private static readonly string[] Common = { "contract.title", "contract.no", "contract.url", "contract.entity", "contract.counterparty", "recipient.name", "actor.name", "app.name" };

    private static string[] With(params string[] extra) => Common.Concat(extra).ToArray();

    private static readonly string[] EmailInApp = { "Email", "InApp" };

    public static readonly IReadOnlyList<NotificationEventInfo> All = new List<NotificationEventInfo>
    {
        new(NoticeReminder, "Notice reminder", "ReminderRecipient", EmailInApp, new[] { "ReminderRecipient" },
            "Renewal decision needed: {{contract.title}}",
            "Contract {{contract.no}} ({{contract.title}}) with {{contract.counterparty}} has its notice deadline on {{deadline}} ({{days}} days). Record a renewal decision: {{contract.url}}",
            With("deadline", "days")),
        new(Escalation, "Escalation to the owner", "Owner", EmailInApp, new[] { "Owner" },
            "Escalation: no renewal decision for {{contract.title}}",
            "Contract {{contract.no}} ({{contract.title}}) has its notice deadline on {{deadline}} and no renewal decision has been recorded. {{contract.url}}",
            With("deadline", "days")),
        new(ObligationDue, "Obligation due", "Responsible", EmailInApp, new[] { "Responsible" },
            "Obligation due on {{contract.title}}",
            "An obligation on contract {{contract.no}} ({{contract.title}}) is due on {{deadline}}. {{contract.url}}",
            With("deadline", "days")),
        new(AnnualReview, "Annual review (open-ended contracts)", "ReminderRecipient", EmailInApp, new[] { "ReminderRecipient" },
            "Annual review: {{contract.title}}",
            "Contract {{contract.no}} ({{contract.title}}) is open-ended; review whether it should continue. {{contract.url}}",
            With("deadline", "days")),
        new(ApprovalRequested, "Approval requested", "Approver", EmailInApp, new[] { "Approver" },
            "Approval requested: {{contract.title}}",
            "Please review contract {{contract.no}} ({{contract.title}}) with {{contract.counterparty}} as {{step.role}}. {{contract.url}}",
            With("step.role", "comment")),
        new(ApprovalDecided, "Approval decided", "Owner", EmailInApp, new[] { "Owner" },
            "Approval decided: {{contract.title}}",
            "The approval of contract {{contract.no}} ({{contract.title}}) was decided. {{comment}} {{contract.url}}",
            With("step.role", "comment")),
        new(ApprovalDelegated, "Approval delegated", "Approver", EmailInApp, new[] { "Approver" },
            "Approval delegated to you: {{contract.title}}",
            "{{actor.name}} delegated the approval of contract {{contract.no}} ({{contract.title}}) to you. {{contract.url}}",
            With("step.role", "comment")),
        new(RenewalDecision, "Renewal decision recorded", "Owner", new[] { "InApp" }, new[] { "Owner", "Watchers" },
            "Renewal decision on {{contract.title}}",
            "{{actor.name}} recorded a renewal decision on contract {{contract.no}} ({{contract.title}}). {{contract.url}}",
            With("deadline", "days", "comment")),
        new(Mention, "Mentioned in a comment", "Mentioned", EmailInApp, new[] { "Mentioned" },
            "{{actor.name}} mentioned you on {{contract.title}}",
            "{{actor.name}} wrote on contract {{contract.no}} ({{contract.title}}): {{comment}} {{contract.url}}",
            With("comment")),
        new(WatchedContractChanged, "Watched contract changed", "Watchers", new[] { "InApp" }, new[] { "Watchers" },
            "{{contract.title}} was updated",
            "{{actor.name}} changed contract {{contract.no}} ({{contract.title}}): {{comment}} {{contract.url}}",
            With("comment")),
        new(DataCompletionRequested, "Data completion requested", "Owner", EmailInApp, new[] { "Owner", "Delegate" },
            "Please complete the data of {{contract.title}}",
            "{{actor.name}} asks you to complete contract {{contract.no}} ({{contract.title}}). Missing: {{missing}}. {{message}} {{contract.url}}",
            With("missing", "message")),
        new(ImportFinished, "Import finished", "Admins", new[] { "InApp" }, new[] { "Admins" },
            "Import of {{import.file}} finished",
            "{{import.created}} created, {{import.failed}} failed ({{source.name}}).",
            new[] { "recipient.name", "actor.name", "app.name", "import.file", "import.created", "import.failed", "source.name" }),
        new(SyncFailed, "Sync failed", "Admins", EmailInApp, new[] { "Admins" },
            "Sync failed: {{source.name}}",
            "The sync source {{source.name}} failed: {{message}}",
            new[] { "recipient.name", "actor.name", "app.name", "source.name", "import.file", "import.created", "import.failed", "message" }),
        // Release 4: e-signature. Natural recipients: the internal signers (Requested); the owner,
        // the delegate and the sender (Completed, Declined; Declined also covers an expired request).
        new(SignatureRequested, "Signature requested", "Approver", EmailInApp, new[] { "Approver" },
            "Please sign: {{contract.title}}",
            "{{actor.name}} asks you to sign contract {{contract.no}} ({{contract.title}}) with {{contract.counterparty}}. You receive the signing link from the e-signature provider. {{comment}} {{contract.url}}",
            With("step.role", "comment", "signer.name")),
        new(SignatureCompleted, "Signature completed", "Owner", EmailInApp, new[] { "Owner", "Watchers" },
            "Signed: {{contract.title}}",
            "Contract {{contract.no}} ({{contract.title}}) has been signed by everyone. The signed document is in the contract's documents. {{contract.url}}",
            With("step.role", "comment", "signer.name")),
        new(SignatureDeclined, "Signature declined or expired", "Owner", EmailInApp, new[] { "Owner" },
            "Signature not completed: {{contract.title}}",
            "The signature request for contract {{contract.no}} ({{contract.title}}) was not completed. {{comment}} {{contract.url}}",
            With("step.role", "comment", "signer.name"))
    };

    public static NotificationEventInfo? Find(string eventName) => All.FirstOrDefault(e => e.Event == eventName);

    /// <summary>Notification.Kind (release 2 names included) -> event.</summary>
    public static string EventFor(string kind)
    {
        if (kind.StartsWith("Notice", StringComparison.Ordinal))
        {
            return NoticeReminder;
        }
        switch (kind)
        {
            case ContractCalculations.ObligationKind: return ObligationDue;
            case "ApprovalRejected":
            case "ApprovalCompleted":
                return ApprovalDecided;
            default:
                return kind;
        }
    }

    private static readonly Regex PlaceholderPattern = new(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.CultureInvariant);

    public static List<string> PlaceholdersIn(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }
        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!result.Contains(name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>Replaces {{name}} with the value; unknown names are left as they are.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        return PlaceholderPattern.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return values.TryGetValue(name, out var value) ? value : match.Value;
        });
    }

    public static Dictionary<string, string> SampleValues() => new()
    {
        ["contract.title"] = "Supply agreement, EPDM rubber compounds",
        ["contract.no"] = "C-2026-0042",
        ["contract.url"] = "https://contracts.example/contracts/00000000-0000-0000-0000-000000000000",
        ["contract.entity"] = "DAFA A/S",
        ["contract.counterparty"] = "Nordic Polymer Compounds AB",
        ["recipient.name"] = "Jonas Berg",
        ["actor.name"] = "Mette Holm",
        ["app.name"] = "DAFA Contracts",
        ["deadline"] = "1 Mar 2027",
        ["days"] = "45",
        ["step.role"] = "Department head",
        ["comment"] = "Looks good to me.",
        ["missing"] = "Notice period, Signed date",
        ["message"] = "Please complete before the board meeting.",
        ["import.file"] = "contracts.xlsx",
        ["import.created"] = "812",
        ["import.failed"] = "3",
        ["source.name"] = "ERP vendors",
        ["signer.name"] = "Anna Lindqvist"
    };
}

/// <summary>Background jobs (CONTRACT_V3 section 9.7).</summary>
public static class JobKeys
{
    public const string Reminders = "Reminders";
    public const string Obligations = "Obligations";
    public const string ContractEnd = "ContractEnd";
    public const string Digest = "Digest";
    public const string CounterpartySync = "CounterpartySync";
    public const string DirectorySync = "DirectorySync";
    public const string UploadCleanup = "UploadCleanup";
    public const string DataRoomCleanup = "DataRoomCleanup";
    /// <summary>Release 4: polls the e-signature provider, sends reminders, expires old requests.</summary>
    public const string SignatureSync = "SignatureSync";
    /// <summary>SharePoint hardening: the nightly storage reconciliation report.</summary>
    public const string StorageReconciliation = "StorageReconciliation";

    public static readonly (string Key, string Label, string Description, ScheduleFrequency Frequency, string Time, int? Day)[] All =
    {
        (Reminders, "Notice reminders", "Notice reminders, escalations, annual reviews and holiday cover rerouting.", ScheduleFrequency.Hourly, "06:00", null),
        (Obligations, "Obligation reminders", "Reminds the responsible person before an obligation is due.", ScheduleFrequency.Hourly, "06:00", null),
        (ContractEnd, "Contract end", "Ends expired contracts and rolls auto-renewing contracts into the next term.", ScheduleFrequency.Daily, "01:00", null),
        (Digest, "Digest", "Daily and weekly digest e-mails.", ScheduleFrequency.Daily, "07:00", null),
        (CounterpartySync, "Counterparty sync", "Runs the sync sources that are due and the supplier master source.", ScheduleFrequency.Hourly, "06:00", null),
        (DirectorySync, "Directory sync", "Brings users up to date from the directory.", ScheduleFrequency.Daily, "05:00", null),
        (UploadCleanup, "Upload cleanup", "Deletes unused intake uploads after 7 days and expired import sessions; extracts document text for search.", ScheduleFrequency.Hourly, "06:00", null),
        (DataRoomCleanup, "Data room cleanup", "Deletes data room exports after they expire.", ScheduleFrequency.Daily, "03:00", null),
        (SignatureSync, "E-signature sync", "Asks the e-signature provider for the status of open requests, reminds signers and expires requests.", ScheduleFrequency.Hourly, "06:00", null),
        (StorageReconciliation, "Storage reconciliation", "Compares the registered documents with the files in storage and reports missing files, unknown files and differences. Nothing is repaired.", ScheduleFrequency.Daily, "02:00", null)
    };
}

/// <summary>Admin settings: general settings, notification settings, jobs.</summary>
public class SettingsService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public SettingsService(IAppDbContext db, IClock clock, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    // ---------- general settings ----------

    public Task<AppSettingsDto> GetAppSettingsAsync(CancellationToken cancellationToken = default) =>
        AppSettingsStore.LoadAsync(_db, cancellationToken);

    public async Task<AppSettingsDto> UpdateAppSettingsAsync(AppSettingsDto input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            throw new InputValidationException("appName", "Send the settings.");
        }
        if (string.IsNullOrWhiteSpace(input.AppName) || input.AppName.Trim().Length > 60)
            ContractValidator.Add(errors, "appName", "App name is required (at most 60 characters).");
        if (!AppSettingsStore.DateFormats.Contains(input.DateFormat))
            ContractValidator.Add(errors, "dateFormat", "Unknown date format.");
        if (!AppSettingsStore.NumberFormats.Contains(input.NumberFormat))
            ContractValidator.Add(errors, "numberFormat", "Unknown number format.");
        if (input.SessionTimeoutMinutes < 15 || input.SessionTimeoutMinutes > 1440)
            ContractValidator.Add(errors, "sessionTimeoutMinutes", "Session timeout must be between 15 and 1440 minutes.");
        if (input.DdThresholdDkk < 0)
            ContractValidator.Add(errors, "ddThresholdDkk", "The threshold cannot be negative.");
        // Release 4: omitted = unchanged (older clients do not send it).
        var current = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        current.CounterpartyCreation ??= "AdminOnly";
        var counterpartyCreation = current.CounterpartyCreation;
        if (!string.IsNullOrWhiteSpace(input.CounterpartyCreation))
        {
            var normalized = AppSettingsStore.NormalizeCounterpartyCreation(input.CounterpartyCreation);
            if (normalized is null)
                ContractValidator.Add(errors, "counterpartyCreation", "Use AdminOnly, ContractOwners or AllUsers.");
            else
                counterpartyCreation = normalized;
        }
        var landing = input.LandingPageByRole ?? new Dictionary<string, string>();
        foreach (var role in AppSettingsStore.Roles)
        {
            if (!landing.TryGetValue(role, out var page) || !AppSettingsStore.LandingPages.Contains(page))
                ContractValidator.Add(errors, "landingPageByRole", "Choose a landing page for every role (" + role + ").");
        }
        var activeKeys = await _db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive).Select(d => d.Key).ToListAsync(cancellationToken);
        foreach (var field in input.DataQualityFields ?? new List<string>())
        {
            var ok = DataQuality.IsStandardKey(field)
                || (field.StartsWith("cf.", StringComparison.Ordinal) && activeKeys.Contains(field.Substring(3)));
            if (!ok)
                ContractValidator.Add(errors, "dataQualityFields", "Unknown data-quality field: " + field + ".");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        var stored = new AppSettingsDto
        {
            AppName = input.AppName.Trim(),
            DateFormat = input.DateFormat,
            NumberFormat = input.NumberFormat,
            LandingPageByRole = AppSettingsStore.Roles.ToDictionary(r => r, r => landing[r]),
            Features = input.Features ?? new FeatureTogglesDto(),
            SessionTimeoutMinutes = input.SessionTimeoutMinutes,
            DataQualityFields = (input.DataQualityFields ?? new List<string>()).Distinct().ToList(),
            DdThresholdDkk = input.DdThresholdDkk,
            CounterpartyCreation = counterpartyCreation
        };
        await SaveSettingAsync(AppSettingsStore.Key, JsonDefaults.Write(stored), cancellationToken);
        _audit.Record("settings.general", "Updated general settings"
            + (counterpartyCreation != current.CounterpartyCreation ? " (counterparties may be created by: " + counterpartyCreation + ")" : ""));
        await _db.SaveChangesAsync(cancellationToken);
        return await AppSettingsStore.LoadAsync(_db, cancellationToken);
    }

    private async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            row = new AppSetting { Key = key };
            _db.AppSettings.Add(row);
        }
        row.Value = value;
        row.UpdatedAt = _clock.UtcNow;
    }

    // ---------- notification settings ----------

    public static NotificationEventSettingDto ToDto(NotificationEventInfo info, NotificationSetting? row)
    {
        return new NotificationEventSettingDto
        {
            Event = info.Event,
            Label = info.Label,
            Enabled = row?.Enabled ?? true,
            Channels = row is null ? info.Channels.ToList() : NotificationRouting.SplitList(row.Channels),
            Recipients = row is null ? info.Recipients.ToList() : NotificationRouting.SplitList(row.Recipients),
            ExtraRecipientIds = row is null ? new List<Guid>() : NotificationRouting.SplitIds(row.ExtraRecipientIds),
            Subject = row?.SubjectTemplate ?? info.Subject,
            Body = row?.BodyTemplate ?? info.Body,
            Placeholders = info.Placeholders.ToList()
        };
    }

    public async Task<NotificationSettingsDto> GetNotificationSettingsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.NotificationSettings.AsNoTracking().ToListAsync(cancellationToken);
        var schedules = await _db.ReminderSchedules.AsNoTracking().ToListAsync(cancellationToken);
        var result = new NotificationSettingsDto
        {
            DigestDefault = await AppSettingsStore.LoadDigestDefaultAsync(_db, cancellationToken)
        };
        foreach (var info in NotificationCatalog.All)
        {
            result.Events.Add(ToDto(info, rows.FirstOrDefault(r => r.EventType == info.Event)));
        }
        foreach (var schedule in schedules)
        {
            var settings = ReminderSettingsReader.FromSchedule(schedule);
            result.ReminderSchedules.Add(new TypeReminderScheduleDto
            {
                TypeId = schedule.ContractTypeId,
                NoticeDays = settings.GetNoticeDays(),
                EscalationDays = schedule.EscalationDays
            });
        }
        return result;
    }

    public async Task<NotificationSettingsDto> UpdateNotificationSettingsAsync(NotificationSettingsDto input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        var events = input?.Events ?? new List<NotificationEventSettingDto>();
        foreach (var info in NotificationCatalog.All)
        {
            var count = events.Count(e => e is not null && e.Event == info.Event);
            if (count != 1)
                ContractValidator.Add(errors, "events", "Every event must be listed exactly once (" + info.Event + ").");
        }
        foreach (var item in events.Where(e => e is not null))
        {
            var info = NotificationCatalog.Find(item.Event);
            if (info is null)
            {
                ContractValidator.Add(errors, "events", "Unknown event: " + item.Event + ".");
                continue;
            }
            var prefix = "events." + item.Event + ".";
            if (string.IsNullOrWhiteSpace(item.Subject) || item.Subject.Length > 200)
                ContractValidator.Add(errors, prefix + "subject", "The subject is required (at most 200 characters).");
            if (string.IsNullOrWhiteSpace(item.Body) || item.Body.Length > 4000)
                ContractValidator.Add(errors, prefix + "body", "The text is required (at most 4000 characters).");
            foreach (var name in NotificationCatalog.PlaceholdersIn(item.Subject))
            {
                if (!info.Placeholders.Contains(name))
                    ContractValidator.Add(errors, prefix + "subject", "Unknown placeholder {{" + name + "}}.");
            }
            foreach (var name in NotificationCatalog.PlaceholdersIn(item.Body))
            {
                if (!info.Placeholders.Contains(name))
                    ContractValidator.Add(errors, prefix + "body", "Unknown placeholder {{" + name + "}}.");
            }
            foreach (var channel in item.Channels ?? new List<string>())
            {
                if (!NotificationCatalog.ChannelNames.Contains(channel))
                    ContractValidator.Add(errors, prefix + "channels", "Unknown channel: " + channel + ".");
            }
            foreach (var rule in item.Recipients ?? new List<string>())
            {
                if (!NotificationCatalog.RecipientRules.Contains(rule))
                    ContractValidator.Add(errors, prefix + "recipients", "Unknown recipient rule: " + rule + ".");
            }
            foreach (var userId in item.ExtraRecipientIds ?? new List<Guid>())
            {
                if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
                    ContractValidator.Add(errors, prefix + "extraRecipientIds", "Unknown user.");
            }
        }
        if (input?.DigestDefault is not null && input.DigestDefault != "Off" && input.DigestDefault != "Daily" && input.DigestDefault != "Weekly")
            ContractValidator.Add(errors, "digestDefault", "Use Off, Daily or Weekly.");
        var schedules = input?.ReminderSchedules ?? new List<TypeReminderScheduleDto>();
        var typeIds = (await _db.ContractTypes.AsNoTracking().Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        var seenTypes = new HashSet<Guid>();
        foreach (var schedule in schedules.Where(s => s is not null))
        {
            if (!typeIds.Contains(schedule.TypeId))
                ContractValidator.Add(errors, "reminderSchedules", "Unknown contract type.");
            if (!seenTypes.Add(schedule.TypeId))
                ContractValidator.Add(errors, "reminderSchedules", "A contract type can have only one schedule.");
            var days = schedule.NoticeDays ?? new List<int>();
            if (days.Count < 1 || days.Count > 5 || days.Any(d => d <= 0) || days.Distinct().Count() != days.Count)
                ContractValidator.Add(errors, "reminderSchedules", "Notice days: 1 to 5 different positive numbers.");
            if (schedule.EscalationDays < 0)
                ContractValidator.Add(errors, "reminderSchedules", "Escalation days cannot be negative.");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        var rows = await _db.NotificationSettings.ToListAsync(cancellationToken);
        foreach (var item in events)
        {
            var row = rows.FirstOrDefault(r => r.EventType == item.Event);
            if (row is null)
            {
                row = new NotificationSetting { Id = Guid.NewGuid(), EventType = item.Event };
                _db.NotificationSettings.Add(row);
                rows.Add(row);
            }
            row.Enabled = item.Enabled;
            row.Channels = string.Join(",", (item.Channels ?? new List<string>()).Distinct());
            row.Recipients = string.Join(",", (item.Recipients ?? new List<string>()).Distinct());
            row.ExtraRecipientIds = string.Join(",", (item.ExtraRecipientIds ?? new List<Guid>()).Distinct());
            row.SubjectTemplate = item.Subject.Trim();
            row.BodyTemplate = item.Body.Trim();
        }
        await SaveSettingAsync(AppSettingsStore.DigestDefaultKey, input!.DigestDefault ?? "Weekly", cancellationToken);

        var existing = await _db.ReminderSchedules.ToListAsync(cancellationToken);
        _db.ReminderSchedules.RemoveRange(existing.Where(e => !schedules.Any(s => s.TypeId == e.ContractTypeId)));
        foreach (var schedule in schedules)
        {
            var row = existing.FirstOrDefault(e => e.ContractTypeId == schedule.TypeId);
            if (row is null)
            {
                row = new ReminderSchedule { Id = Guid.NewGuid(), ContractTypeId = schedule.TypeId };
                _db.ReminderSchedules.Add(row);
            }
            row.NoticeDays = string.Join(",", schedule.NoticeDays.Distinct().OrderByDescending(d => d));
            row.EscalationDays = schedule.EscalationDays;
        }
        _audit.Record("settings.notifications", "Updated notification settings and reminder schedules");
        await _db.SaveChangesAsync(cancellationToken);
        return await GetNotificationSettingsAsync(cancellationToken);
    }

    public TemplatePreviewDto Preview(TemplatePreviewInput input)
    {
        var info = NotificationCatalog.Find(input?.Event ?? "");
        if (info is null)
        {
            throw new InputValidationException("event", "Unknown event.");
        }
        var samples = NotificationCatalog.SampleValues();
        var result = new TemplatePreviewDto
        {
            Subject = NotificationCatalog.Render(input!.Subject ?? "", samples),
            Body = NotificationCatalog.Render(input.Body ?? "", samples)
        };
        foreach (var name in NotificationCatalog.PlaceholdersIn(input.Subject).Concat(NotificationCatalog.PlaceholdersIn(input.Body)))
        {
            if (!info.Placeholders.Contains(name) && !result.Unknown.Contains(name))
            {
                result.Unknown.Add(name);
            }
        }
        return result;
    }

    // ---------- jobs ----------

    public static async Task<List<JobSchedule>> EnsureJobSchedulesAsync(IAppDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var rows = await db.JobSchedules.ToListAsync(cancellationToken);
        var added = false;
        foreach (var job in JobKeys.All)
        {
            if (rows.Any(r => r.JobKey == job.Key))
            {
                continue;
            }
            var row = new JobSchedule
            {
                JobKey = job.Key,
                Frequency = job.Frequency,
                Time = job.Time,
                DayOfWeek = job.Day,
                NextRunAt = ScheduleCalculator.NextRun(job.Frequency, job.Time, job.Day, clock.UtcNow)
            };
            // Hourly jobs are due at once on a new database, like the release 2 hourly run.
            if (job.Frequency == ScheduleFrequency.Hourly)
            {
                row.NextRunAt = clock.UtcNow;
            }
            db.JobSchedules.Add(row);
            rows.Add(row);
            added = true;
        }
        if (added)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return rows;
    }

    public static JobInfoDto ToDto(JobSchedule row)
    {
        var job = JobKeys.All.FirstOrDefault(j => j.Key == row.JobKey);
        return new JobInfoDto
        {
            Key = row.JobKey,
            Label = job.Label ?? row.JobKey,
            Description = job.Description ?? "",
            Schedule = new ScheduleDto { Frequency = row.Frequency.ToString(), Time = row.Time, DayOfWeek = row.DayOfWeek },
            LastRun = row.LastRunAt is null ? null : new JobLastRunDto
            {
                At = row.LastRunAt.Value,
                Status = row.LastStatus ?? "Succeeded",
                Message = row.LastMessage,
                DurationMs = row.LastDurationMs ?? 0
            },
            NextRunAt = row.NextRunAt
        };
    }

    public async Task<List<JobInfoDto>> GetJobsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await EnsureJobSchedulesAsync(_db, _clock, cancellationToken);
        var order = JobKeys.All.Select(j => j.Key).ToList();
        return rows.Where(r => order.Contains(r.JobKey)).OrderBy(r => order.IndexOf(r.JobKey)).Select(ToDto).ToList();
    }

    /// <summary>Validates a schedule (shared by jobs and sync sources). Returns (frequency, time, day).</summary>
    public static (ScheduleFrequency Frequency, string Time, int? Day) ValidateSchedule(ScheduleDto? schedule, string field, Dictionary<string, string[]> errors)
    {
        if (schedule is null)
        {
            ContractValidator.Add(errors, field, "Send a schedule.");
            return (ScheduleFrequency.Off, "06:00", null);
        }
        if (!Enum.TryParse<ScheduleFrequency>(schedule.Frequency ?? "", false, out var frequency) || !Enum.IsDefined(typeof(ScheduleFrequency), frequency))
        {
            ContractValidator.Add(errors, field + ".frequency", "Use Off, Hourly, Daily or Weekly.");
            return (ScheduleFrequency.Off, "06:00", null);
        }
        var time = string.IsNullOrWhiteSpace(schedule.Time) ? "06:00" : schedule.Time.Trim();
        if ((frequency == ScheduleFrequency.Daily || frequency == ScheduleFrequency.Weekly) && !ScheduleCalculator.TryParseTime(time, out _))
            ContractValidator.Add(errors, field + ".time", "Use the format HH:mm.");
        int? day = null;
        if (frequency == ScheduleFrequency.Weekly)
        {
            if (schedule.DayOfWeek is null || schedule.DayOfWeek < 1 || schedule.DayOfWeek > 7)
                ContractValidator.Add(errors, field + ".dayOfWeek", "Choose a day from 1 (Monday) to 7 (Sunday).");
            day = schedule.DayOfWeek;
        }
        return (frequency, time, day);
    }

    public async Task<JobInfoDto> UpdateJobAsync(string key, JobScheduleInput input, CancellationToken cancellationToken = default)
    {
        await EnsureJobSchedulesAsync(_db, _clock, cancellationToken);
        var row = await _db.JobSchedules.FirstOrDefaultAsync(j => j.JobKey == key, cancellationToken)
            ?? throw new NotFoundException("Unknown job.");
        var errors = new Dictionary<string, string[]>();
        var (frequency, time, day) = ValidateSchedule(input?.Schedule, "schedule", errors);
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        row.Frequency = frequency;
        row.Time = time;
        row.DayOfWeek = day;
        row.NextRunAt = ScheduleCalculator.NextRun(frequency, time, day, _clock.UtcNow);
        _audit.Record("settings.jobs", "Job " + row.JobKey + " schedule: " + frequency + (frequency == ScheduleFrequency.Daily || frequency == ScheduleFrequency.Weekly ? " " + time : ""));
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }
}
