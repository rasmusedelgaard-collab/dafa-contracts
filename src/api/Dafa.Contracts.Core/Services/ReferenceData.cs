using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Data-quality checks (CONTRACT_V3 section 1.7): key -> label.</summary>
public static class DataQuality
{
    public const string NoticePeriodDays = "noticePeriodDays";
    public const string ContractValue = "contractValue";
    public const string SignedDate = "signedDate";
    public const string ChangeOfControl = "changeOfControl";
    public const string SignedDocument = "signedDocument";
    public const string EndDate = "endDate";
    public const string ResponsibleId = "responsibleId";
    public const string PaymentTerms = "paymentTerms";
    public const string GoverningLaw = "governingLaw";

    public static readonly (string Key, string Label)[] Standard =
    {
        (NoticePeriodDays, ContractCalculations.MissingNoticePeriod),
        (ContractValue, ContractCalculations.MissingValue),
        (SignedDate, ContractCalculations.MissingSignedDate),
        (ChangeOfControl, ContractCalculations.MissingChangeOfControl),
        (SignedDocument, "Signed document"),
        (EndDate, "End date"),
        (ResponsibleId, "Responsible"),
        (PaymentTerms, "Payment terms"),
        (GoverningLaw, "Governing law")
    };

    /// <summary>The release 2 list (= the default).</summary>
    public static readonly string[] Defaults = { NoticePeriodDays, ContractValue, SignedDate, ChangeOfControl };

    public static bool IsStandardKey(string key) => Standard.Any(s => s.Key == key);

    public static string? StandardLabel(string key) => Standard.Where(s => s.Key == key).Select(s => s.Label).FirstOrDefault();

    public static bool IsLive(Contract contract) =>
        contract.Status == ContractStatus.Active || contract.Status == ContractStatus.NoticeGiven;
}

/// <summary>
/// Per-request reference data: names, settings, currency rates, data-quality inputs and (for the
/// contracts of one page) tags, custom field values and the caller's watches. Loaded once and
/// joined in memory.
/// </summary>
public sealed class ReferenceData
{
    public Dictionary<Guid, string> Entities { get; private set; } = new();
    public Dictionary<Guid, LegalEntity> EntityRows { get; private set; } = new();
    public Dictionary<Guid, string> Departments { get; private set; } = new();
    public Dictionary<Guid, string> Types { get; private set; } = new();
    public Dictionary<Guid, AppUser> Users { get; private set; } = new();
    public AppSettingsDto Settings { get; private set; } = AppSettingsStore.Defaults();
    public IReadOnlyDictionary<string, decimal> Rates { get; private set; } = new Dictionary<string, decimal>();
    /// <summary>Contracts with at least one document of kind Main (hasSignedDocument).</summary>
    public HashSet<Guid> MainDocumentContracts { get; private set; } = new();
    /// <summary>Active custom field definitions.</summary>
    public List<CustomFieldDefinition> CustomFields { get; private set; } = new();
    /// <summary>(contract, field) pairs that have a value, for the custom fields in the data-quality list.</summary>
    private HashSet<(Guid ContractId, Guid FieldId)> _customPresence = new();

    // Loaded with LoadExtrasAsync for the contracts of one page.
    private Dictionary<Guid, List<TagRefDto>> _tags = new();
    private Dictionary<Guid, Dictionary<string, object?>> _customValues = new();
    private HashSet<Guid> _watched = new();

    public static async Task<ReferenceData> LoadAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var data = new ReferenceData();
        var entities = await db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken);
        data.EntityRows = entities.ToDictionary(e => e.Id, e => e);
        data.Entities = entities.ToDictionary(e => e.Id, e => e.Name);
        data.Departments = (await db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        data.Types = (await db.ContractTypes.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(t => t.Id, t => t.Name);
        data.Users = (await db.Users.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(u => u.Id, u => u);
        data.Settings = await AppSettingsStore.LoadAsync(db, cancellationToken);
        data.Rates = await CurrencyTable.LoadRatesAsync(db, cancellationToken);
        data.MainDocumentContracts = (await db.Documents.AsNoTracking()
            .Where(d => d.Kind == DocumentKind.Main)
            .Select(d => d.ContractId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        data.CustomFields = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);

        var customKeys = data.Settings.DataQualityFields
            .Where(k => k.StartsWith("cf.", StringComparison.Ordinal))
            .Select(k => k.Substring(3))
            .ToList();
        var customIds = data.CustomFields.Where(d => customKeys.Contains(d.Key)).Select(d => d.Id).ToList();
        if (customIds.Count > 0)
        {
            var pairs = await db.CustomFieldValues.AsNoTracking()
                .Where(v => customIds.Contains(v.FieldId))
                .Select(v => new { v.ContractId, v.FieldId })
                .ToListAsync(cancellationToken);
            data._customPresence = pairs.Select(p => (p.ContractId, p.FieldId)).ToHashSet();
        }
        return data;
    }

    /// <summary>Loads tags, custom field values and the caller's watches for the given contracts (one page).</summary>
    public async Task LoadExtrasAsync(IAppDbContext db, IReadOnlyCollection<Guid> contractIds, Guid? userId, CancellationToken cancellationToken = default)
    {
        if (userId is not null)
        {
            var user = userId.Value;
            _watched = (await db.ContractWatchers.AsNoTracking().Where(w => w.UserId == user).Select(w => w.ContractId).ToListAsync(cancellationToken)).ToHashSet();
        }
        if (contractIds.Count == 0)
        {
            return;
        }
        var tags = (await db.Tags.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(t => t.Id);
        var links = new List<ContractTag>();
        var values = new List<CustomFieldValue>();
        foreach (var chunk in contractIds.Distinct().Chunk(500))
        {
            var ids = chunk.ToList();
            if (tags.Count > 0)
            {
                links.AddRange(await db.ContractTags.AsNoTracking().Where(t => ids.Contains(t.ContractId)).ToListAsync(cancellationToken));
            }
            if (CustomFields.Count > 0)
            {
                values.AddRange(await db.CustomFieldValues.AsNoTracking().Where(v => ids.Contains(v.ContractId)).ToListAsync(cancellationToken));
            }
        }
        _tags = new Dictionary<Guid, List<TagRefDto>>();
        foreach (var link in links)
        {
            if (!tags.TryGetValue(link.TagId, out var tag))
            {
                continue;
            }
            if (!_tags.TryGetValue(link.ContractId, out var list))
            {
                list = new List<TagRefDto>();
                _tags[link.ContractId] = list;
            }
            list.Add(new TagRefDto { Id = tag.Id, Name = tag.Name, Color = tag.Color });
        }
        foreach (var list in _tags.Values)
        {
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        var definitions = CustomFields.ToDictionary(d => d.Id);
        _customValues = new Dictionary<Guid, Dictionary<string, object?>>();
        foreach (var value in values)
        {
            if (!definitions.TryGetValue(value.FieldId, out var definition))
            {
                continue;
            }
            if (!_customValues.TryGetValue(value.ContractId, out var map))
            {
                map = new Dictionary<string, object?>();
                _customValues[value.ContractId] = map;
            }
            map[definition.Key] = CustomFieldService.RawValue(definition, value);
        }
    }

    public string Entity(Guid id) => Entities.TryGetValue(id, out var name) ? name : "";
    public string Department(Guid id) => Departments.TryGetValue(id, out var name) ? name : "";
    public string Type(Guid id) => Types.TryGetValue(id, out var name) ? name : "";
    public string UserName(Guid? id) => id is not null && Users.TryGetValue(id.Value, out var user) ? user.Name : "";
    public string UserInitials(Guid? id) => id is not null && Users.TryGetValue(id.Value, out var user) ? user.Initials : "";

    public List<TagRefDto> TagsFor(Guid contractId) =>
        _tags.TryGetValue(contractId, out var list) ? list : new List<TagRefDto>();

    public Dictionary<string, object?> CustomValuesFor(Guid contractId) =>
        _customValues.TryGetValue(contractId, out var map) ? map : new Dictionary<string, object?>();

    public bool IsWatched(Guid contractId) => _watched.Contains(contractId);

    public bool HasSignedDocument(Contract contract) => MainDocumentContracts.Contains(contract.Id);

    // ---------- money ----------

    /// <summary>An amount in DKK with the admin-editable rates, rounded to whole DKK (null = 0).</summary>
    public decimal Dkk(decimal? amount, string? currency) =>
        decimal.Round(CurrencyRates.ToDkk(amount, currency, Rates), 0, MidpointRounding.AwayFromZero);

    public decimal AnnualDkk(Contract contract) => Dkk(contract.AnnualValue, contract.Currency);
    public decimal TotalDkk(Contract contract) => Dkk(contract.TotalValue, contract.Currency);

    /// <summary>max(total, annual) in DKK, as for approvals (value bands, thresholds).</summary>
    public decimal ContractValueDkk(Contract contract) =>
        Dkk(Math.Max(contract.TotalValue ?? 0m, contract.AnnualValue ?? 0m), contract.Currency);

    // ---------- data quality ----------

    public static bool Applies(CustomFieldDefinition definition, Guid typeId)
    {
        var types = JsonDefaultsList(definition.ContractTypeIdsJson);
        return types.Count == 0 || types.Contains(typeId);
    }

    private static List<Guid> JsonDefaultsList(string? json) => JsonDefaults.ReadList<Guid>(json);

    /// <summary>
    /// Missing data as (key, label): the always-on release 2 checks (title, counterparty, currency,
    /// renewal period) and the data-quality fields from General settings.
    /// </summary>
    public List<(string Key, string Label)> MissingEntries(Contract contract)
    {
        var result = new List<(string Key, string Label)>();
        if (string.IsNullOrWhiteSpace(contract.Title)) result.Add(("title", ContractCalculations.MissingTitle));
        if (string.IsNullOrWhiteSpace(contract.CounterpartyName) && contract.SupplierId is null) result.Add(("counterparty", ContractCalculations.MissingCounterparty));
        if (string.IsNullOrWhiteSpace(contract.Currency)) result.Add(("currency", ContractCalculations.MissingCurrency));
        if (contract.AutoRenew && (contract.RenewalMonths is null || contract.RenewalMonths <= 0)) result.Add(("renewalMonths", ContractCalculations.MissingRenewalPeriod));

        var live = DataQuality.IsLive(contract);
        foreach (var key in Settings.DataQualityFields)
        {
            switch (key)
            {
                case DataQuality.NoticePeriodDays:
                    if (contract.NoticePeriodDays is null) result.Add((key, ContractCalculations.MissingNoticePeriod));
                    break;
                case DataQuality.ContractValue:
                    if (contract.AnnualValue is null && contract.TotalValue is null) result.Add((key, ContractCalculations.MissingValue));
                    break;
                case DataQuality.SignedDate:
                    if (live && contract.SignedDate is null) result.Add((key, ContractCalculations.MissingSignedDate));
                    break;
                case DataQuality.ChangeOfControl:
                    if (contract.ChangeOfControl is null) result.Add((key, ContractCalculations.MissingChangeOfControl));
                    break;
                case DataQuality.SignedDocument:
                    if (live && !HasSignedDocument(contract)) result.Add((key, "Signed document"));
                    break;
                case DataQuality.EndDate:
                    if (contract.EndDate is null) result.Add((key, "End date"));
                    break;
                case DataQuality.ResponsibleId:
                    if (contract.ResponsibleId is null) result.Add((key, "Responsible"));
                    break;
                case DataQuality.PaymentTerms:
                    if (string.IsNullOrWhiteSpace(contract.PaymentTerms)) result.Add((key, "Payment terms"));
                    break;
                case DataQuality.GoverningLaw:
                    if (string.IsNullOrWhiteSpace(contract.GoverningLaw)) result.Add((key, "Governing law"));
                    break;
                default:
                    if (key.StartsWith("cf.", StringComparison.Ordinal))
                    {
                        var fieldKey = key.Substring(3);
                        var definition = CustomFields.FirstOrDefault(d => d.Key == fieldKey);
                        if (definition is not null && Applies(definition, contract.TypeId) && !_customPresence.Contains((contract.Id, definition.Id)))
                        {
                            result.Add((key, definition.Label));
                        }
                    }
                    break;
            }
        }
        return result;
    }

    /// <summary>Missing-data labels of a contract (the `missingData` array).</summary>
    public List<string> Missing(Contract contract) => MissingEntries(contract).Select(e => e.Label).ToList();
}

/// <summary>Currency rates from the Currencies table (admin-editable), falling back to the fixed rates.</summary>
public static class CurrencyTable
{
    public static async Task<IReadOnlyDictionary<string, decimal>> LoadRatesAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var rows = await db.Currencies.AsNoTracking().ToListAsync(cancellationToken);
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.RateToDkk > 0m)
            {
                rates[row.Code] = row.RateToDkk;
            }
        }
        return rates;
    }

    /// <summary>Active codes; the fixed list when the table is empty.</summary>
    public static async Task<List<string>> ActiveCodesAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var rows = await db.Currencies.AsNoTracking().Where(c => c.IsActive).ToListAsync(cancellationToken);
        if (rows.Count == 0 && !await db.Currencies.AnyAsync(cancellationToken))
        {
            return CurrencyRates.Currencies.ToList();
        }
        return rows.OrderBy(c => c.SortOrder).ThenBy(c => c.Code, StringComparer.Ordinal).Select(c => c.Code).ToList();
    }
}
