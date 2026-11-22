using System.Globalization;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Version comparison of key terms (CONTRACT_V3 section 6): versions are rebuilt from the audit
/// trail by starting from the current contract and undoing the recorded old/new values (structured
/// DataJson from release 3 on; the release 2 text "Label: "old" -> "new"; ..." is parsed for older
/// events). Status per version comes from the status events.
/// </summary>
public class VersionService
{
    /// <summary>Key terms in display order: field key (as in the contract body) and label.</summary>
    public static readonly (string Field, string Label)[] KeyTerms =
    {
        ("title", "Title"), ("counterpartyName", "Counterparty"), ("typeId", "Type"), ("entityId", "Legal entity"),
        ("departmentId", "Department"), ("ownerId", "Owner"), ("startDate", "Start date"), ("endDate", "End date"),
        ("noticePeriodDays", "Notice period"), ("autoRenew", "Automatic renewal"), ("renewalMonths", "Renewal period"),
        ("annualValue", "Annual value"), ("totalValue", "Total value"), ("currency", "Currency"), ("paymentTerms", "Payment terms"),
        ("priceAdjustment", "Price adjustment"), ("priceAdjustmentDate", "Next price adjustment"), ("governingLaw", "Governing law"),
        ("liabilityCap", "Liability cap"), ("exclusivity", "Exclusivity"), ("changeOfControl", "Change of control"), ("status", "Status")
    };

    private static readonly Dictionary<string, string> StatusAfter = new(StringComparer.Ordinal)
    {
        ["approval.started"] = "In approval",
        ["approval.completed"] = "Approved",
        ["approval.rejected"] = "Draft",
        ["contract.signed"] = "Active",
        ["contract.expired"] = "Expired",
        ["contract.terminated"] = "Terminated"
    };

    private static readonly HashSet<string> ChangeActions = new(StringComparer.Ordinal)
    {
        "contract.updated", "renewal.renewed", "renewal.automatic", "renewal.decided", "import.contract-updated", "contract.reassigned"
    };

    private static readonly Regex ChangePattern = new("(?<label>[^;:\"]+): \"(?<old>.*?)\" -> \"(?<new>.*?)\"(?=; |$)", RegexOptions.CultureInvariant);

    private readonly IAppDbContext _db;
    private readonly IContractAccessService _access;

    public VersionService(IAppDbContext db, IContractAccessService access)
    {
        _db = db;
        _access = access;
    }

    private sealed class Version
    {
        public ContractVersionDto Dto { get; init; } = new();
        public Dictionary<string, string?> Values { get; init; } = new();
    }

    private static string StatusLabel(ContractStatus status) => status switch
    {
        ContractStatus.InApproval => "In approval",
        ContractStatus.NoticeGiven => "Notice given",
        _ => status.ToString()
    };

    /// <summary>Diff display text -> comparison value: "(empty)" is null, "open-ended" is "Open-ended".</summary>
    private static string? Clean(string? value)
    {
        if (value is null || value == "(empty)")
        {
            return null;
        }
        return value == "open-ended" ? "Open-ended" : value;
    }

    private static List<FieldChangeDto> ChangesOf(AuditEvent auditEvent)
    {
        if (!string.IsNullOrWhiteSpace(auditEvent.DataJson))
        {
            return JsonDefaults.ReadList<FieldChangeDto>(auditEvent.DataJson);
        }
        var result = new List<FieldChangeDto>();
        if (auditEvent.Action != "contract.updated")
        {
            return result;
        }
        // Release 2 text: "Updated contract details. Title: "A" -> "B"; End date: "x" -> "y"".
        var text = auditEvent.Text;
        var start = text.IndexOf(". ", StringComparison.Ordinal);
        if (start < 0)
        {
            return result;
        }
        foreach (Match match in ChangePattern.Matches(text.Substring(start + 2)))
        {
            var label = match.Groups["label"].Value.Trim();
            var field = LabelToField(label);
            if (field is not null)
            {
                result.Add(new FieldChangeDto { Field = field, Label = label, Old = match.Groups["old"].Value, New = match.Groups["new"].Value });
            }
        }
        return result;
    }

    /// <summary>The labels ContractChanges.Diff writes, mapped back to field keys.</summary>
    private static string? LabelToField(string label) => label switch
    {
        "Title" => "title",
        "Type" => "typeId",
        "Supplier" => "supplierId",
        "Counterparty" => "counterpartyName",
        "Legal entity" => "entityId",
        "Department" => "departmentId",
        "Owner" => "ownerId",
        "Delegate" => "delegateId",
        "Responsible" => "responsibleId",
        "Reminder recipient" => "reminderRecipient",
        "Start date" => "startDate",
        "End date" => "endDate",
        "Notice period (days)" => "noticePeriodDays",
        "Auto-renew" => "autoRenew",
        "Renewal period (months)" => "renewalMonths",
        "Annual value" => "annualValue",
        "Total value" => "totalValue",
        "Currency" => "currency",
        "Payment terms" => "paymentTerms",
        "Price adjustment" => "priceAdjustment",
        "Price adjustment date" => "priceAdjustmentDate",
        "Governing law" => "governingLaw",
        "Liability cap" => "liabilityCap",
        "Exclusivity" => "exclusivity",
        "Change of control" => "changeOfControl",
        "Summary" => "summary",
        _ => null
    };

    private async Task<(List<Version> Versions, List<(string Field, string Label)> Fields)> BuildAsync(Guid contractId, CancellationToken cancellationToken)
    {
        Contract contract;
        try
        {
            contract = await _access.GetReadableAsync(contractId, false, cancellationToken);
        }
        catch (ForbiddenException)
        {
            throw new NotFoundException("Contract not found.");
        }
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var supplierNames = new Dictionary<Guid, string>();
        if (contract.SupplierId is not null)
        {
            var supplierId = contract.SupplierId.Value;
            var name = await _db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken);
            if (name is not null) supplierNames[supplierId] = name;
        }
        var snapshot = ContractChanges.Snapshot(contract, reference, supplierNames);
        var state = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (field, _) in KeyTerms)
        {
            state[field] = field == "status" ? StatusLabel(contract.Status) : Clean(snapshot.TryGetValue(field, out var value) ? value : null);
        }
        var fields = KeyTerms.ToList();
        await reference.LoadExtrasAsync(_db, new[] { contract.Id }, null, cancellationToken);
        var custom = reference.CustomValuesFor(contract.Id);
        foreach (var definition in reference.CustomFields.OrderBy(d => d.SortOrder).ThenBy(d => d.Label))
        {
            var key = "cf." + definition.Key;
            fields.Add((key, definition.Label));
            state[key] = custom.TryGetValue(definition.Key, out var raw) && raw is not null ? Clean(CustomFieldService.DisplayRaw(raw, null)) : null;
        }

        var events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ContractId == contractId)
            .OrderBy(e => e.At)
            .ToListAsync(cancellationToken);
        var created = events.FirstOrDefault(e => e.Action == "contract.created" || e.Action == "import.contract-created");

        // Status after each event, replayed forward from the initial status.
        var initialStatus = "Draft";
        if (created?.Action == "import.contract-created" && !string.IsNullOrEmpty(created.DataJson))
        {
            initialStatus = JsonDefaults.ReadList<FieldChangeDto>(created.DataJson).FirstOrDefault(c => c.Field == "status")?.New ?? "Draft";
        }
        var status = initialStatus;
        var statusAfter = new Dictionary<Guid, string>();
        var changeEvents = new List<(AuditEvent Event, List<FieldChangeDto> Changes)>();
        foreach (var auditEvent in events)
        {
            var changes = ChangeActions.Contains(auditEvent.Action)
                ? ChangesOf(auditEvent).Where(c => state.ContainsKey(c.Field)).ToList()
                : new List<FieldChangeDto>();
            var statusChanged = false;
            if (StatusAfter.TryGetValue(auditEvent.Action, out var next))
            {
                statusChanged = next != status;
                status = next;
            }
            else if (auditEvent.Action == "renewal.decided" && auditEvent.Text.StartsWith("Notice given", StringComparison.Ordinal))
            {
                statusChanged = status != "Notice given";
                status = "Notice given";
            }
            statusAfter[auditEvent.Id] = status;
            if (changes.Count > 0 || statusChanged)
            {
                changeEvents.Add((auditEvent, changes));
            }
        }

        // Undo, newest first.
        var versions = new List<Version>();
        var lastEvent = events.LastOrDefault();
        versions.Add(new Version
        {
            Dto = new ContractVersionDto
            {
                Id = "current",
                At = contract.UpdatedAt,
                Who = lastEvent?.UserName ?? reference.UserName(contract.CreatedById),
                Label = "Current",
                ChangedFields = new List<string>()
            },
            Values = new Dictionary<string, string?>(state)
        });
        for (var i = changeEvents.Count - 1; i >= 0; i--)
        {
            var (auditEvent, changes) = changeEvents[i];
            var changedFields = changes.Select(c => c.Field).Distinct().ToList();
            var previousStatus = i > 0 ? statusAfter[changeEvents[i - 1].Event.Id] : initialStatus;
            if (statusAfter[auditEvent.Id] != previousStatus && !changedFields.Contains("status"))
            {
                changedFields.Add("status");
            }
            var after = new Dictionary<string, string?>(state) { ["status"] = statusAfter[auditEvent.Id] };
            if (i < changeEvents.Count - 1)
            {
                versions.Add(new Version
                {
                    Dto = new ContractVersionDto
                    {
                        Id = auditEvent.Id.ToString(),
                        At = auditEvent.At,
                        Who = auditEvent.UserName,
                        Label = auditEvent.Text.Length > 120 ? auditEvent.Text.Substring(0, 120) + "..." : auditEvent.Text,
                        ChangedFields = changedFields
                    },
                    Values = after
                });
            }
            else
            {
                // The newest change equals "current"; it only tells which fields changed last.
                versions[0].Dto.ChangedFields = changedFields;
            }
            foreach (var change in changes)
            {
                state[change.Field] = Clean(change.Old);
            }
            state["status"] = previousStatus;
        }
        versions.Add(new Version
        {
            Dto = new ContractVersionDto
            {
                Id = "created",
                At = created?.At ?? contract.CreatedAt,
                Who = created?.UserName ?? reference.UserName(contract.CreatedById),
                Label = "Created",
                ChangedFields = new List<string>()
            },
            Values = new Dictionary<string, string?>(state) { ["status"] = initialStatus }
        });
        return (versions, fields);
    }

    public async Task<List<ContractVersionDto>> GetVersionsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var (versions, _) = await BuildAsync(contractId, cancellationToken);
        return versions.Select(v => v.Dto).ToList();
    }

    public async Task<ContractComparisonDto> CompareAsync(Guid contractId, string? from, string? to, CancellationToken cancellationToken = default)
    {
        var (versions, fields) = await BuildAsync(contractId, cancellationToken);
        var a = versions.FirstOrDefault(v => v.Dto.Id == (from ?? ""))
            ?? throw new NotFoundException("Unknown version.");
        var b = versions.FirstOrDefault(v => v.Dto.Id == (to ?? ""))
            ?? throw new NotFoundException("Unknown version.");
        var result = new ContractComparisonDto { From = a.Dto, To = b.Dto };
        foreach (var (field, label) in fields)
        {
            a.Values.TryGetValue(field, out var left);
            b.Values.TryGetValue(field, out var right);
            result.Fields.Add(new ComparedFieldDto { Field = field, Label = label, From = left, To = right, Changed = !string.Equals(left, right, StringComparison.Ordinal) });
        }
        return result;
    }
}
