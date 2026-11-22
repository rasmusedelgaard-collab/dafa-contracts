using System.Globalization;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Required-field rules for saving a contract (brief section 4).</summary>
public static class ContractValidator
{
    public static Dictionary<string, string[]> Validate(ContractInput input)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(input.Title)) Add(errors, "title", "Title is required.");
        else if (input.Title.Trim().Length > 200) Add(errors, "title", "Title can be at most 200 characters.");
        if (input.TypeId is null) Add(errors, "typeId", "Contract type is required.");
        if (input.SupplierId is null && string.IsNullOrWhiteSpace(input.CounterpartyName))
            Add(errors, "counterpartyName", "Counterparty is required: pick a supplier or enter a name.");
        else if (input.CounterpartyName is not null && input.CounterpartyName.Trim().Length > 300)
            Add(errors, "counterpartyName", "Counterparty can be at most 300 characters.");
        if (input.EntityId is null) Add(errors, "entityId", "Legal entity is required.");
        if (input.DepartmentId is null) Add(errors, "departmentId", "Department is required.");
        if (input.OwnerId is null) Add(errors, "ownerId", "Owner is required.");

        if (input.ReminderRecipient is null) Add(errors, "reminderRecipient", "Choose who receives reminders.");
        else if (input.ReminderRecipient == ReminderRecipient.Delegate && input.DelegateId is null)
            Add(errors, "reminderRecipient", "Reminders go to the delegate, but no delegate is selected.");
        else if (input.ReminderRecipient == ReminderRecipient.Responsible && input.ResponsibleId is null)
            Add(errors, "reminderRecipient", "Reminders go to the responsible, but no responsible is selected.");

        if (input.StartDate is null) Add(errors, "startDate", "Start date is required.");
        if (input.EndDate is null && input.OpenEnded == false)
            Add(errors, "endDate", "Enter an end date or mark the contract as open-ended.");
        if (input.EndDate is not null && input.StartDate is not null && input.EndDate.Value < input.StartDate.Value)
            Add(errors, "endDate", "End date cannot be before the start date.");

        if (input.NoticePeriodDays is null) Add(errors, "noticePeriodDays", "Notice period is required (use 0 when there is none).");
        else if (input.NoticePeriodDays.Value < 0 || input.NoticePeriodDays.Value > 3650)
            Add(errors, "noticePeriodDays", "Notice period must be between 0 and 3650 days.");

        if (input.AutoRenew is null) Add(errors, "autoRenew", "State whether the contract renews automatically.");
        if (input.RenewalMonths is not null && (input.RenewalMonths.Value < 1 || input.RenewalMonths.Value > 240))
            Add(errors, "renewalMonths", "Renewal period must be between 1 and 240 months.");

        if (string.IsNullOrWhiteSpace(input.Currency)) Add(errors, "currency", "Currency is required.");
        else if (!CurrencyRates.IsKnown(input.Currency.Trim())) Add(errors, "currency", "Unknown currency.");

        if (input.AnnualValue is null && input.TotalValue is null)
            Add(errors, "annualValue", "Enter an annual value or a total value.");
        if (input.AnnualValue is < 0m) Add(errors, "annualValue", "Annual value cannot be negative.");
        if (input.TotalValue is < 0m) Add(errors, "totalValue", "Total value cannot be negative.");

        if (input.ChangeOfControl is null) Add(errors, "changeOfControl", "State whether the contract has a change of control clause.");

        return errors;
    }

    public static void Add(Dictionary<string, string[]> errors, string field, string message)
    {
        if (errors.TryGetValue(field, out var existing))
        {
            errors[field] = existing.Concat(new[] { message }).ToArray();
        }
        else
        {
            errors[field] = new[] { message };
        }
    }

    /// <summary>The stored contract as an input, to re-check required fields before approval.</summary>
    public static ContractInput ToInput(Contract contract)
    {
        return new ContractInput
        {
            Title = contract.Title,
            TypeId = contract.TypeId,
            SupplierId = contract.SupplierId,
            CounterpartyName = contract.CounterpartyName,
            EntityId = contract.EntityId,
            DepartmentId = contract.DepartmentId,
            OwnerId = contract.OwnerId,
            DelegateId = contract.DelegateId,
            ResponsibleId = contract.ResponsibleId,
            ReminderRecipient = contract.ReminderRecipient,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            OpenEnded = contract.EndDate is null,
            NoticePeriodDays = contract.NoticePeriodDays,
            AutoRenew = contract.AutoRenew,
            RenewalMonths = contract.RenewalMonths,
            AnnualValue = contract.AnnualValue,
            TotalValue = contract.TotalValue,
            Currency = contract.Currency,
            ChangeOfControl = contract.ChangeOfControl
        };
    }
}

/// <summary>
/// H1: once a contract has left Draft, the terms the approval was based on are locked.
/// Only an administrator may change them, and every such change is audited field by field.
/// </summary>
public static class ApprovedTerms
{
    public const string LockedMessage =
        "Approved terms are locked. Register an amendment as a linked contract, or ask an administrator.";

    /// <summary>Request field names (camelCase) that are locked after approval; sent to the UI as lockedFields.</summary>
    public static readonly string[] LockedFields =
    {
        "title", "typeId", "supplierId", "counterpartyName", "entityId", "departmentId",
        "startDate", "endDate", "openEnded", "noticePeriodDays", "autoRenew", "renewalMonths",
        "annualValue", "totalValue", "currency", "liabilityCap", "exclusivity", "changeOfControl",
        "governingLaw", "priceAdjustment", "priceAdjustmentDate"
    };

    public static bool IsLockedField(string field) => Array.IndexOf(LockedFields, field) >= 0;

    /// <summary>True from the moment the contract is submitted: everything except Draft.</summary>
    public static bool AppliesTo(ContractStatus status) => status != ContractStatus.Draft;

    private static bool Filled(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// True when a locked field holds a value on the stored contract. An empty locked field is
    /// missing data: someone with edit rights may fill it in once, after which it is locked like
    /// the rest. Fields that cannot be empty (type, entity, department, dates, auto-renew) always
    /// count as filled; an open-ended contract (no end date) is a value, not a gap.
    /// The two value fields go together: once either annual or total value is set, both are
    /// locked, because the approval is based on the larger of the two and filling in the other
    /// one could raise it.
    /// </summary>
    public static bool HasValue(Contract contract, string field)
    {
        switch (field)
        {
            case "title": return Filled(contract.Title);
            case "counterpartyName": return Filled(contract.CounterpartyName);
            case "supplierId": return contract.SupplierId is not null;
            case "noticePeriodDays": return contract.NoticePeriodDays is not null;
            case "renewalMonths": return contract.RenewalMonths is not null;
            case "annualValue":
            case "totalValue": return contract.AnnualValue is not null || contract.TotalValue is not null;
            case "currency": return Filled(contract.Currency);
            case "liabilityCap": return Filled(contract.LiabilityCap);
            case "exclusivity": return Filled(contract.Exclusivity);
            case "governingLaw": return Filled(contract.GoverningLaw);
            case "priceAdjustment": return Filled(contract.PriceAdjustment);
            case "priceAdjustmentDate": return contract.PriceAdjustmentDate is not null;
            case "changeOfControl": return contract.ChangeOfControl is not null;
            default: return true;
        }
    }

    /// <summary>
    /// The fields a user may not change on this contract right now: for a non-administrator on a
    /// contract that has left Draft, the locked fields that currently have a value.
    /// </summary>
    public static List<string> LockedFieldsFor(AccessProfile profile, Contract contract)
    {
        if (profile.IsAdmin || !AppliesTo(contract.Status))
        {
            return new List<string>();
        }
        return LockedFields.Where(field => HasValue(contract, field)).ToList();
    }
}

public sealed record FieldChange(string Field, string Label, string Old, string New);

/// <summary>Compares a stored contract with the values an update would give it.</summary>
public static class ContractChanges
{
    private const string Empty = "(empty)";

    private static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Empty;
        }
        var text = value.Trim().Replace("\r", " ").Replace("\n", " ");
        return text.Length > 80 ? text.Substring(0, 80) + "..." : text;
    }

    private static string Date(DateOnly? value) =>
        value is null ? Empty : value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Number(decimal? value) =>
        value is null ? Empty : value.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Whole(int? value) =>
        value is null ? Empty : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string YesNo(bool? value) => value is null ? Empty : (value.Value ? "Yes" : "No");

    private static string Name(string name) => string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;

    private static void Add(List<FieldChange> changes, string field, string label, bool same, string oldText, string newText)
    {
        if (!same)
        {
            changes.Add(new FieldChange(field, label, oldText, newText));
        }
    }

    public static List<FieldChange> Diff(
        Contract current, Contract proposed, ReferenceData reference, IReadOnlyDictionary<Guid, string> supplierNames)
    {
        string Supplier(Guid? id) =>
            id is null ? Empty : (supplierNames.TryGetValue(id.Value, out var name) ? name : id.Value.ToString());
        string User(Guid? id) => id is null ? Empty : Name(reference.UserName(id));

        var changes = new List<FieldChange>();
        Add(changes, "title", "Title", current.Title == proposed.Title, Text(current.Title), Text(proposed.Title));
        Add(changes, "typeId", "Type", current.TypeId == proposed.TypeId, Name(reference.Type(current.TypeId)), Name(reference.Type(proposed.TypeId)));
        Add(changes, "supplierId", "Supplier", current.SupplierId == proposed.SupplierId, Supplier(current.SupplierId), Supplier(proposed.SupplierId));
        Add(changes, "counterpartyName", "Counterparty", current.CounterpartyName == proposed.CounterpartyName, Text(current.CounterpartyName), Text(proposed.CounterpartyName));
        Add(changes, "entityId", "Legal entity", current.EntityId == proposed.EntityId, Name(reference.Entity(current.EntityId)), Name(reference.Entity(proposed.EntityId)));
        Add(changes, "departmentId", "Department", current.DepartmentId == proposed.DepartmentId, Name(reference.Department(current.DepartmentId)), Name(reference.Department(proposed.DepartmentId)));
        Add(changes, "ownerId", "Owner", current.OwnerId == proposed.OwnerId, User(current.OwnerId), User(proposed.OwnerId));
        Add(changes, "delegateId", "Delegate", current.DelegateId == proposed.DelegateId, User(current.DelegateId), User(proposed.DelegateId));
        Add(changes, "responsibleId", "Responsible", current.ResponsibleId == proposed.ResponsibleId, User(current.ResponsibleId), User(proposed.ResponsibleId));
        Add(changes, "reminderRecipient", "Reminder recipient", current.ReminderRecipient == proposed.ReminderRecipient, current.ReminderRecipient.ToString(), proposed.ReminderRecipient.ToString());
        Add(changes, "startDate", "Start date", current.StartDate == proposed.StartDate, Date(current.StartDate), Date(proposed.StartDate));
        Add(changes, "endDate", "End date", current.EndDate == proposed.EndDate,
            current.EndDate is null ? "open-ended" : Date(current.EndDate), proposed.EndDate is null ? "open-ended" : Date(proposed.EndDate));
        Add(changes, "noticePeriodDays", "Notice period (days)", current.NoticePeriodDays == proposed.NoticePeriodDays, Whole(current.NoticePeriodDays), Whole(proposed.NoticePeriodDays));
        Add(changes, "autoRenew", "Auto-renew", current.AutoRenew == proposed.AutoRenew, YesNo(current.AutoRenew), YesNo(proposed.AutoRenew));
        Add(changes, "renewalMonths", "Renewal period (months)", current.RenewalMonths == proposed.RenewalMonths, Whole(current.RenewalMonths), Whole(proposed.RenewalMonths));
        Add(changes, "annualValue", "Annual value", current.AnnualValue == proposed.AnnualValue, Number(current.AnnualValue), Number(proposed.AnnualValue));
        Add(changes, "totalValue", "Total value", current.TotalValue == proposed.TotalValue, Number(current.TotalValue), Number(proposed.TotalValue));
        Add(changes, "currency", "Currency", current.Currency == proposed.Currency, Text(current.Currency), Text(proposed.Currency));
        Add(changes, "paymentTerms", "Payment terms", current.PaymentTerms == proposed.PaymentTerms, Text(current.PaymentTerms), Text(proposed.PaymentTerms));
        Add(changes, "priceAdjustment", "Price adjustment", current.PriceAdjustment == proposed.PriceAdjustment, Text(current.PriceAdjustment), Text(proposed.PriceAdjustment));
        Add(changes, "priceAdjustmentDate", "Price adjustment date", current.PriceAdjustmentDate == proposed.PriceAdjustmentDate, Date(current.PriceAdjustmentDate), Date(proposed.PriceAdjustmentDate));
        Add(changes, "governingLaw", "Governing law", current.GoverningLaw == proposed.GoverningLaw, Text(current.GoverningLaw), Text(proposed.GoverningLaw));
        Add(changes, "liabilityCap", "Liability cap", current.LiabilityCap == proposed.LiabilityCap, Text(current.LiabilityCap), Text(proposed.LiabilityCap));
        Add(changes, "exclusivity", "Exclusivity", current.Exclusivity == proposed.Exclusivity, Text(current.Exclusivity), Text(proposed.Exclusivity));
        Add(changes, "changeOfControl", "Change of control", current.ChangeOfControl == proposed.ChangeOfControl, YesNo(current.ChangeOfControl), YesNo(proposed.ChangeOfControl));
        Add(changes, "summary", "Summary", current.Summary == proposed.Summary, Text(current.Summary), Text(proposed.Summary));
        return changes;
    }

    /// <summary>"Title: "A" -> "B"; End date: "2027-03-31" -> "2028-03-31"".</summary>
    public static string Describe(IEnumerable<FieldChange> changes) =>
        string.Join("; ", changes.Select(c => c.Label + ": \"" + c.Old + "\" -> \"" + c.New + "\""));
}

/// <summary>Reminder rows that belong to one notice deadline (Notice{days} and Escalation).</summary>
public static class NoticeCycle
{
    /// <summary>
    /// Removes the sent and unsent notice and escalation rows of a contract, so the reminder cycle
    /// starts over for a new notice deadline. Adds to the unit of work; the caller saves.
    /// </summary>
    public static async Task<int> RemoveAsync(IAppDbContext db, Guid contractId, CancellationToken cancellationToken = default)
    {
        var rows = await db.Reminders
            .Where(r => r.ContractId == contractId && r.ObligationId == null)
            .ToListAsync(cancellationToken);
        var cycle = rows.Where(r => ContractCalculations.IsNoticeCycleKind(r.Kind)).ToList();
        if (cycle.Count > 0)
        {
            db.Reminders.RemoveRange(cycle);
        }
        return cycle.Count;
    }
}

public class ContractCommandService
{
    /// <summary>Pending intake uploads one user may have at a time (they are stored in the database).</summary>
    public const int MaxPendingUploadsPerUser = 20;

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly ApprovalService _approvals;
    private readonly DocumentWriter _documents;

    public ContractCommandService(
        IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock,
        AuditService audit, ApprovalService approvals, DocumentWriter documents)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _audit = audit;
        _approvals = approvals;
        _documents = documents;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string FormatContractNo(int year, int sequence) =>
        "C-" + year.ToString("0000") + "-" + sequence.ToString("0000");

    private async Task<string> NextContractNoAsync(CancellationToken cancellationToken)
    {
        var year = _clock.Today.Year;
        var prefix = "C-" + year.ToString("0000") + "-";
        var numbers = await _db.Contracts.AsNoTracking()
            .Where(c => c.ContractNo.StartsWith(prefix))
            .Select(c => c.ContractNo)
            .ToListAsync(cancellationToken);
        var max = 0;
        foreach (var number in numbers)
        {
            if (number.Length > prefix.Length && int.TryParse(number.Substring(prefix.Length), out var sequence) && sequence > max)
            {
                max = sequence;
            }
        }
        return FormatContractNo(year, max + 1);
    }

    /// <summary>Validates the input, including that referenced rows exist, and returns the supplier name if one is chosen.</summary>
    private async Task<string?> ValidateAsync(ContractInput input, CancellationToken cancellationToken)
    {
        var errors = ContractValidator.Validate(input);
        string? supplierName = null;

        if (input.TypeId is not null)
        {
            var typeId = input.TypeId.Value;
            if (!await _db.ContractTypes.AnyAsync(t => t.Id == typeId, cancellationToken))
                ContractValidator.Add(errors, "typeId", "Unknown contract type.");
        }
        if (input.EntityId is not null)
        {
            var entityId = input.EntityId.Value;
            if (!await _db.LegalEntities.AnyAsync(e => e.Id == entityId, cancellationToken))
                ContractValidator.Add(errors, "entityId", "Unknown legal entity.");
        }
        if (input.DepartmentId is not null)
        {
            var departmentId = input.DepartmentId.Value;
            if (!await _db.Departments.AnyAsync(d => d.Id == departmentId, cancellationToken))
                ContractValidator.Add(errors, "departmentId", "Unknown department.");
        }
        await CheckUserAsync(errors, "ownerId", input.OwnerId, cancellationToken);
        await CheckUserAsync(errors, "delegateId", input.DelegateId, cancellationToken);
        await CheckUserAsync(errors, "responsibleId", input.ResponsibleId, cancellationToken);
        if (input.SupplierId is not null)
        {
            var supplierId = input.SupplierId.Value;
            var supplier = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);
            if (supplier is null)
                ContractValidator.Add(errors, "supplierId", "Unknown supplier.");
            else
                supplierName = supplier.Name;
        }

        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        return supplierName;
    }

    private async Task CheckUserAsync(Dictionary<string, string[]> errors, string field, Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return;
        }
        var id = userId.Value;
        if (!await _db.Users.AnyAsync(u => u.Id == id && u.IsActive, cancellationToken))
        {
            ContractValidator.Add(errors, field, "Unknown or inactive user.");
        }
    }

    private static void Apply(ContractInput input, Contract contract, string? supplierName)
    {
        contract.Title = input.Title!.Trim();
        contract.TypeId = input.TypeId!.Value;
        contract.SupplierId = input.SupplierId;
        contract.CounterpartyName = Clean(input.CounterpartyName) ?? supplierName ?? "";
        contract.EntityId = input.EntityId!.Value;
        contract.DepartmentId = input.DepartmentId!.Value;
        contract.OwnerId = input.OwnerId!.Value;
        contract.DelegateId = input.DelegateId;
        contract.ResponsibleId = input.ResponsibleId;
        contract.ReminderRecipient = input.ReminderRecipient!.Value;
        contract.StartDate = input.StartDate!.Value;
        contract.EndDate = input.OpenEnded == true ? null : input.EndDate;
        contract.NoticePeriodDays = input.NoticePeriodDays;
        contract.AutoRenew = input.AutoRenew!.Value;
        contract.RenewalMonths = input.RenewalMonths;
        contract.AnnualValue = input.AnnualValue;
        contract.TotalValue = input.TotalValue;
        contract.Currency = input.Currency!.Trim().ToUpperInvariant();
        contract.PaymentTerms = Clean(input.PaymentTerms);
        contract.PriceAdjustment = Clean(input.PriceAdjustment);
        contract.PriceAdjustmentDate = input.PriceAdjustmentDate;
        contract.GoverningLaw = Clean(input.GoverningLaw);
        contract.LiabilityCap = Clean(input.LiabilityCap);
        contract.Exclusivity = Clean(input.Exclusivity);
        contract.ChangeOfControl = input.ChangeOfControl;
        contract.Summary = Clean(input.Summary);
    }

    public async Task<Guid> CreateAsync(ContractInput input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!ContractAccessRules.CanCreate(profile))
        {
            throw new ForbiddenException("You need the contract owner role to create contracts.");
        }
        var supplierName = await ValidateAsync(input, cancellationToken);
        var now = _clock.UtcNow;
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Status = ContractStatus.Draft,
            CreatedById = profile.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        Apply(input, contract, supplierName);
        if (!ContractAccessRules.CanRead(profile, contract))
        {
            throw new InputValidationException("ownerId",
                "You would not be able to see this contract after saving. Make yourself owner, delegate or responsible, or use one of your own departments.");
        }
        contract.ContractNo = await NextContractNoAsync(cancellationToken);
        _db.Contracts.Add(contract);

        if (input.UploadId is not null)
        {
            var uploadId = input.UploadId.Value;
            var upload = await _db.PendingUploads.FirstOrDefaultAsync(u => u.Id == uploadId, cancellationToken);
            if (upload is null || upload.CreatedById != profile.UserId)
            {
                throw new InputValidationException("uploadId", "The uploaded file was not found. Upload it again.");
            }
            await _documents.AddAsync(contract, upload.FileName, DocumentKind.Main, upload.Content, profile.UserId, cancellationToken);
            contract.DeviationsJson = upload.DeviationsJson;
            if (contract.Summary is null)
            {
                contract.Summary = upload.Summary;
            }
            _db.PendingUploads.Remove(upload);
        }

        var created = _audit.Record("contract.created", "Created draft " + contract.ContractNo, contract.Id);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // Retry only when the number was taken by a draft created at the same time.
                var number = contract.ContractNo;
                var id = contract.Id;
                var taken = await _db.Contracts.AsNoTracking().AnyAsync(c => c.ContractNo == number && c.Id != id, cancellationToken);
                if (!taken)
                {
                    throw;
                }
                contract.ContractNo = await NextContractNoAsync(cancellationToken);
                created.Text = "Created draft " + contract.ContractNo;
            }
        }
        return contract.Id;
    }

    private async Task<Dictionary<Guid, string>> SupplierNamesAsync(Guid? first, Guid? second, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        if (first is not null) ids.Add(first.Value);
        if (second is not null && !ids.Contains(second.Value)) ids.Add(second.Value);
        var names = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            var name = await _db.Suppliers.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken);
            if (name is not null)
            {
                names[id] = name;
            }
        }
        return names;
    }

    public async Task UpdateAsync(Guid contractId, ContractInput input, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetEditableAsync(contractId, cancellationToken);
        if (contract.Status == ContractStatus.InApproval)
        {
            throw new ConflictException("The contract is in approval and cannot be changed until the approval is finished.");
        }
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var supplierName = await ValidateAsync(input, cancellationToken);

        // Work out what would change on a scratch copy first, so a refused update leaves the tracked row untouched.
        var proposed = new Contract();
        Apply(input, proposed, supplierName);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var supplierNames = await SupplierNamesAsync(contract.SupplierId, proposed.SupplierId, cancellationToken);
        var changes = ContractChanges.Diff(contract, proposed, reference, supplierNames);
        var termsLocked = ApprovedTerms.AppliesTo(contract.Status);
        // Changes to approved terms that already had a value, and first-time completion of empty ones.
        var lockedChanges = new List<FieldChange>();
        var completedFields = new List<FieldChange>();
        if (termsLocked)
        {
            foreach (var change in changes.Where(c => ApprovedTerms.IsLockedField(c.Field)))
            {
                if (ApprovedTerms.HasValue(contract, change.Field))
                {
                    lockedChanges.Add(change);
                }
                else
                {
                    completedFields.Add(change);
                }
            }
        }
        if (lockedChanges.Count > 0 && !profile.IsAdmin)
        {
            throw new ConflictException(
                ApprovedTerms.LockedMessage + " Locked: " + string.Join(", ", lockedChanges.Select(c => c.Label)) + ".",
                ProblemTypes.LockedFields);
        }

        var oldDeadline = ContractCalculations.NoticeDeadline(contract);
        Apply(input, contract, supplierName);
        contract.UpdatedAt = _clock.UtcNow;

        if (changes.Count == 0)
        {
            _audit.Record("contract.updated", "Saved contract details without changes", contract.Id);
        }
        else
        {
            _audit.Record("contract.updated", "Updated contract details. " + ContractChanges.Describe(changes), contract.Id);
        }
        if (lockedChanges.Count > 0)
        {
            _audit.Record("contract.approved-terms-changed",
                "Administrator changed approved terms (status " + contract.Status + "). " + ContractChanges.Describe(lockedChanges),
                contract.Id);
        }
        if (completedFields.Count > 0)
        {
            _audit.Record("contract.missing-data-completed",
                "Completed missing data after approval (status " + contract.Status + "); these fields are now locked. "
                    + ContractChanges.Describe(completedFields),
                contract.Id);
        }

        // H2: a new notice deadline is a new reminder cycle.
        var newDeadline = ContractCalculations.NoticeDeadline(contract);
        if (oldDeadline != newDeadline)
        {
            await ResetNoticeCycleAsync(contract, oldDeadline, newDeadline, cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string DeadlineText(DateOnly? deadline) =>
        deadline is null ? "none" : deadline.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Removes the reminder rows of the old notice deadline and clears a renewal decision that was
    /// made for it, so reminders and the escalation run again for the new deadline. A recorded
    /// notice (status NoticeGiven) is kept: the contract still ends.
    /// </summary>
    private async Task ResetNoticeCycleAsync(Contract contract, DateOnly? oldDeadline, DateOnly? newDeadline, CancellationToken cancellationToken)
    {
        var removed = await NoticeCycle.RemoveAsync(_db, contract.Id, cancellationToken);
        var text = "Notice deadline changed from " + DeadlineText(oldDeadline) + " to " + DeadlineText(newDeadline)
            + "; the reminder cycle starts over (" + removed + " earlier reminders removed)";
        if (contract.Status == ContractStatus.Active && contract.RenewalDecision is not null)
        {
            text += "; the renewal decision " + contract.RenewalDecision + " for the old deadline was cleared";
            contract.RenewalDecision = null;
            contract.RenewalDecidedById = null;
            contract.RenewalDecidedAt = null;
            contract.RenewalComment = null;
        }
        _audit.Record("reminders.reset", text + ".", contract.Id);
    }

    public async Task SubmitAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetEditableAsync(contractId, cancellationToken);
        if (contract.Status != ContractStatus.Draft)
        {
            throw new ConflictException("Only a draft can be submitted for approval.");
        }
        var errors = ContractValidator.Validate(ContractValidator.ToInput(contract));
        var missing = ContractCalculations.MissingData(contract);
        if (missing.Count > 0)
        {
            ContractValidator.Add(errors, "missingData", "Complete the missing data before submitting: " + string.Join(", ", missing) + ".");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var instance = await _approvals.StartAsync(contract, profile.UserId, cancellationToken);
        contract.Status = ContractStatus.InApproval;
        contract.UpdatedAt = _clock.UtcNow;
        _audit.Record("approval.started", "Submitted for approval (rule: " + instance.RuleName + ")", contract.Id);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A second submit at the same moment: the contract's concurrency token or the
            // one-pending-approval index stopped this one. Nothing of it was saved.
            _db.DiscardChanges();
            throw new ConflictException("The contract was changed or submitted by someone else at the same time. Reload and try again.", ProblemTypes.Stale);
        }
    }

    public async Task SignAsync(Guid contractId, SignInput input, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetEditableAsync(contractId, cancellationToken);
        if (contract.Status != ContractStatus.Approved)
        {
            throw new ConflictException("Only an approved contract can be marked as signed.");
        }
        if (input.SignedDate is null)
        {
            throw new InputValidationException("signedDate", "Signed date is required.");
        }
        contract.SignedDate = input.SignedDate;
        contract.SigningMethod = Clean(input.Method) ?? "Manual";
        contract.Status = ContractStatus.Active;
        contract.UpdatedAt = _clock.UtcNow;
        _audit.Record("contract.signed", "Marked as signed (" + contract.SigningMethod + "); contract is active", contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DecideRenewalAsync(Guid contractId, RenewalDecisionInput input, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetReadableAsync(contractId, true, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!profile.IsAdmin && !ContractAccessRules.IsOnContract(profile, contract))
        {
            throw new ForbiddenException("Only the owner, delegate, responsible or an administrator can record a renewal decision.");
        }
        if (contract.Status != ContractStatus.Active)
        {
            throw new ConflictException("A renewal decision can only be recorded on an active contract.");
        }
        if (input.Decision is null || !Enum.IsDefined(typeof(RenewalDecision), input.Decision.Value))
        {
            throw new InputValidationException("decision", "Choose a decision.");
        }

        var decision = input.Decision.Value;
        var today = _clock.Today;
        var now = _clock.UtcNow;
        var culture = CultureInfo.InvariantCulture;
        if ((decision == RenewalDecision.GiveNotice || decision == RenewalDecision.LetExpire) && string.IsNullOrWhiteSpace(input.Comment))
        {
            throw new InputValidationException("comment", "A comment is required when giving notice or letting the contract expire.");
        }

        if (contract.EndDate is null)
        {
            // M5: an open-ended contract has nothing to renew, but notice can be given at any time.
            if (decision != RenewalDecision.GiveNotice)
            {
                throw new ConflictException("An open-ended contract has no renewal to decide. Record 'Give notice' to end it.");
            }
            var noticeDays = Math.Max(0, contract.NoticePeriodDays ?? 0);
            contract.EndDate = today.AddDays(noticeDays);
            RenewalRules.Apply(contract, decision, profile.UserId, now, input.Comment);
            _audit.Record("renewal.decided",
                "Notice given on an open-ended contract; it ends on " + contract.EndDate.Value.ToString("yyyy-MM-dd", culture)
                    + " (" + noticeDays + " days' notice) - " + input.Comment!.Trim(),
                contract.Id);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (decision == RenewalDecision.LetExpire && contract.AutoRenew)
        {
            throw new InputValidationException("decision",
                "This contract renews automatically, so it cannot be left to expire. Give notice instead.");
        }

        DateOnly? newEndDate = null;
        if (decision == RenewalDecision.Renew)
        {
            // M4: one renewal per term, and not before the reminder cycle of that term has started.
            var deadline = ContractCalculations.NoticeDeadline(contract)!.Value;
            if (contract.LastRenewedDeadline == deadline)
            {
                throw new ConflictException("This term has already been renewed.");
            }
            var settings = await ReminderSettingsReader.GetAsync(_db, cancellationToken);
            if (!ContractCalculations.IsInRenewalWindow(deadline, today, settings.GetNoticeDays()))
            {
                throw new ConflictException(
                    "It is too early to renew this term. A renewal can be recorded from the first reminder before the notice deadline ("
                        + deadline.ToString("yyyy-MM-dd", culture) + ").");
            }
            if (input.NewEndDate is not null)
            {
                if (input.NewEndDate.Value <= contract.EndDate.Value)
                {
                    throw new InputValidationException("newEndDate", "The new end date must be after the current end date.");
                }
                newEndDate = input.NewEndDate;
            }
            else if (!(contract.AutoRenew && contract.RenewalMonths is > 0))
            {
                throw new InputValidationException("newEndDate", "Enter the end date of the new term.");
            }
        }

        var rolled = RenewalRules.Apply(contract, decision, profile.UserId, now, input.Comment, newEndDate);
        if (rolled)
        {
            // New term: the reminder cycle starts over.
            await NoticeCycle.RemoveAsync(_db, contract.Id, cancellationToken);
            var text = "Renewed; new end date " + contract.EndDate!.Value.ToString("yyyy-MM-dd", culture);
            if (!string.IsNullOrWhiteSpace(input.Comment))
            {
                text += " - " + input.Comment.Trim();
            }
            _audit.Record("renewal.renewed", text, contract.Id);
        }
        else
        {
            var text = "Renewal decision: " + decision;
            if (!string.IsNullOrWhiteSpace(input.Comment))
            {
                text += " - " + input.Comment.Trim();
            }
            _audit.Record("renewal.decided", text, contract.Id);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }
}
