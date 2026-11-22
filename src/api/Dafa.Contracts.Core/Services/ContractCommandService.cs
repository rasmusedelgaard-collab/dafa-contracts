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

public class ContractCommandService
{
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

        _audit.Record("contract.created", "Created draft " + contract.ContractNo, contract.Id);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Most likely two drafts were given the same number at the same time; take the next one once.
            contract.ContractNo = await NextContractNoAsync(cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return contract.Id;
    }

    public async Task UpdateAsync(Guid contractId, ContractInput input, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetEditableAsync(contractId, cancellationToken);
        if (contract.Status == ContractStatus.InApproval)
        {
            throw new ConflictException("The contract is in approval and cannot be changed until the approval is finished.");
        }
        var supplierName = await ValidateAsync(input, cancellationToken);
        Apply(input, contract, supplierName);
        contract.UpdatedAt = _clock.UtcNow;
        _audit.Record("contract.updated", "Updated contract details", contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
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
        await _db.SaveChangesAsync(cancellationToken);
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
        if (contract.Status != ContractStatus.Active || contract.EndDate is null)
        {
            throw new ConflictException("A renewal decision can only be recorded on an active contract with an end date.");
        }
        if (input.Decision is null)
        {
            throw new InputValidationException("decision", "Choose a decision.");
        }

        var decision = input.Decision.Value;
        if ((decision == RenewalDecision.GiveNotice || decision == RenewalDecision.LetExpire) && string.IsNullOrWhiteSpace(input.Comment))
        {
            throw new InputValidationException("comment", "A comment is required when giving notice or letting the contract expire.");
        }
        var rolled = RenewalRules.Apply(contract, decision, profile.UserId, _clock.UtcNow, input.Comment);
        if (rolled)
        {
            // New term: the reminder cycle starts over.
            var id = contract.Id;
            var old = await _db.Reminders.Where(r => r.ContractId == id && r.ObligationId == null).ToListAsync(cancellationToken);
            _db.Reminders.RemoveRange(old);
            _audit.Record("renewal.renewed", "Renewed; new end date " + contract.EndDate!.Value.ToString("yyyy-MM-dd"), contract.Id);
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
