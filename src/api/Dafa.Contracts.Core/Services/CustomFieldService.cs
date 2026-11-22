using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>A parsed custom field value ready to store. IsEmpty = no value (the row is removed).</summary>
public sealed class ParsedCustomValue
{
    public ParsedCustomValue(CustomFieldDefinition definition)
    {
        Definition = definition;
    }

    public CustomFieldDefinition Definition { get; }
    public bool IsEmpty { get; set; }
    public string? TextValue { get; set; }
    public double? NumberValue { get; set; }
    public DateOnly? DateValue { get; set; }
    public bool? BoolValue { get; set; }
    public Guid? UserValue { get; set; }
}

/// <summary>
/// Custom fields (CONTRACT_V3 section 8.1): definitions, validation of the values in a contract
/// body, storage as EAV rows, raw values for the API; and field rules (section 8.2): required
/// standard fields and defaults per contract type.
/// </summary>
public class CustomFieldService
{
    private static readonly Regex KeyPattern = new("^[a-z][a-zA-Z0-9]{1,39}$", RegexOptions.CultureInvariant);

    /// <summary>OPTIONAL_STANDARD_FIELDS: standard fields an admin can make required per type.</summary>
    public static readonly string[] OptionalStandardFields =
    {
        "delegateId", "responsibleId", "endDate", "annualValue", "totalValue", "paymentTerms",
        "priceAdjustment", "governingLaw", "liabilityCap", "exclusivity", "summary"
    };

    public static readonly string[] DefaultFields = { "currency", "noticePeriodDays", "reminderRecipient", "autoRenew", "renewalMonths" };

    private readonly IAppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUser _currentUser;

    public CustomFieldService(IAppDbContext db, AuditService audit, ICurrentUser currentUser)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
    }

    // ---------- definitions ----------

    public static List<string> ReadOptions(CustomFieldDefinition definition) => JsonDefaults.ReadList<string>(definition.OptionsJson);
    public static List<Guid> ReadTypeIds(CustomFieldDefinition definition) => JsonDefaults.ReadList<Guid>(definition.ContractTypeIdsJson);

    public static CustomFieldDefDto ToDto(CustomFieldDefinition definition) => new()
    {
        Id = definition.Id,
        Key = definition.Key,
        Label = definition.Label,
        Type = definition.FieldType.ToString(),
        ContractTypeIds = ReadTypeIds(definition),
        Required = definition.Required,
        HelpText = definition.HelpText,
        Order = definition.SortOrder,
        Options = ReadOptions(definition),
        ShowInRegister = definition.ShowInRegister,
        ShowInFilters = definition.ShowInFilters,
        IsActive = definition.IsActive
    };

    private static IEnumerable<CustomFieldDefinition> Ordered(IEnumerable<CustomFieldDefinition> definitions) =>
        definitions.OrderBy(d => d.SortOrder).ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>GET custom-fields (any user): active definitions by order.</summary>
    public async Task<List<CustomFieldDefDto>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var all = await _db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);
        return Ordered(all).Select(ToDto).ToList();
    }

    /// <summary>GET admin/custom-fields: all, including inactive.</summary>
    public async Task<List<CustomFieldDefDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _db.CustomFieldDefinitions.AsNoTracking().ToListAsync(cancellationToken);
        return Ordered(all).Select(ToDto).ToList();
    }

    private static bool IsChoice(CustomFieldType type) => type == CustomFieldType.Choice || type == CustomFieldType.MultiChoice;

    private async Task ApplyDefinitionAsync(CustomFieldInput input, CustomFieldDefinition definition, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var key = (input.Key ?? "").Trim();
        if (isNew)
        {
            if (!KeyPattern.IsMatch(key))
                ContractValidator.Add(errors, "key", "Key: a lowercase letter followed by 1-39 letters or digits (camelCase).");
            else if (await _db.CustomFieldDefinitions.AnyAsync(d => d.Key == key, cancellationToken))
                ContractValidator.Add(errors, "key", "Another field already uses this key.");
        }
        else if (!string.IsNullOrEmpty(input.Key) && key != definition.Key)
        {
            ContractValidator.Add(errors, "key", "The key cannot be changed.");
        }
        var label = (input.Label ?? "").Trim();
        if (label.Length < 1 || label.Length > 80)
            ContractValidator.Add(errors, "label", "Label is required (at most 80 characters).");
        var type = definition.FieldType;
        if (!Enum.TryParse<CustomFieldType>((input.Type ?? "").Trim(), false, out type) || !Enum.IsDefined(typeof(CustomFieldType), type))
        {
            ContractValidator.Add(errors, "type", "Type must be Text, Number, Money, Date, YesNo, Choice, MultiChoice or Person.");
            type = definition.FieldType;
        }
        var options = (input.Options ?? new List<string>()).Select(o => (o ?? "").Trim()).ToList();
        if (IsChoice(type))
        {
            if (options.Count == 0)
                ContractValidator.Add(errors, "options", "A choice field needs at least one option.");
            if (options.Any(o => o.Length < 1 || o.Length > 80 || o.Contains('|')))
                ContractValidator.Add(errors, "options", "Each option must be 1-80 characters (no '|').");
            if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                ContractValidator.Add(errors, "options", "Options must be unique.");
        }
        else if (options.Count > 0)
        {
            ContractValidator.Add(errors, "options", "Only Choice and MultiChoice fields have options.");
        }
        var typeIds = (input.ContractTypeIds ?? new List<Guid>()).Distinct().ToList();
        foreach (var typeId in typeIds)
        {
            if (!await _db.ContractTypes.AnyAsync(t => t.Id == typeId, cancellationToken))
                ContractValidator.Add(errors, "contractTypeIds", "Unknown contract type.");
        }
        if (input.HelpText is not null && input.HelpText.Length > 500)
            ContractValidator.Add(errors, "helpText", "Help text can be at most 500 characters.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        if (!isNew)
        {
            var fieldId = definition.Id;
            var inUse = await _db.CustomFieldValues.AnyAsync(v => v.FieldId == fieldId, cancellationToken);
            if (inUse && type != definition.FieldType)
            {
                throw new ConflictException("The type cannot change while contracts have a value for this field.", ProblemTypesV3.InUse);
            }
            if (inUse && IsChoice(definition.FieldType))
            {
                var removed = ReadOptions(definition).Where(o => !options.Contains(o)).ToList();
                foreach (var option in removed)
                {
                    var single = option;
                    var token = "|" + option + "|";
                    var used = await _db.CustomFieldValues.AnyAsync(v => v.FieldId == fieldId
                        && (v.TextValue == single || (v.TextValue != null && v.TextValue.Contains(token))), cancellationToken);
                    if (used)
                    {
                        throw new ConflictException("The option '" + option + "' is used by contracts and cannot be removed.", ProblemTypesV3.InUse);
                    }
                }
            }
        }

        if (isNew)
        {
            definition.Key = key;
        }
        definition.FieldType = type;
        definition.Label = label;
        definition.ContractTypeIdsJson = JsonDefaults.Write(typeIds);
        definition.Required = input.Required ?? false;
        definition.HelpText = string.IsNullOrWhiteSpace(input.HelpText) ? null : input.HelpText.Trim();
        if (input.Order is not null)
        {
            definition.SortOrder = input.Order.Value;
        }
        definition.OptionsJson = IsChoice(type) ? JsonDefaults.Write(options) : null;
        definition.ShowInRegister = input.ShowInRegister ?? false;
        definition.ShowInFilters = input.ShowInFilters ?? false;
        definition.IsActive = input.IsActive ?? true;
    }

    public async Task<CustomFieldDefDto> CreateAsync(CustomFieldInput input, CancellationToken cancellationToken = default)
    {
        var definition = new CustomFieldDefinition { Id = Guid.NewGuid() };
        if (input.Order is null)
        {
            var orders = await _db.CustomFieldDefinitions.Select(d => d.SortOrder).ToListAsync(cancellationToken);
            definition.SortOrder = orders.Count == 0 ? 0 : orders.Max() + 1;
        }
        await ApplyDefinitionAsync(input, definition, true, cancellationToken);
        _db.CustomFieldDefinitions.Add(definition);
        _audit.Record("settings.custom-fields", "Created custom field " + definition.Label + " (" + definition.Key + ", " + definition.FieldType + ")");
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(definition);
    }

    public async Task<CustomFieldDefDto> UpdateAsync(Guid id, CustomFieldInput input, CancellationToken cancellationToken = default)
    {
        var definition = await _db.CustomFieldDefinitions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Custom field not found.");
        await ApplyDefinitionAsync(input, definition, false, cancellationToken);
        _audit.Record("settings.custom-fields", "Updated custom field " + definition.Label + " (" + definition.Key + ")");
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(definition);
    }

    /// <summary>409 in-use when contracts have a value (deactivate instead).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await _db.CustomFieldDefinitions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Custom field not found.");
        if (await _db.CustomFieldValues.AnyAsync(v => v.FieldId == id, cancellationToken))
        {
            throw new ConflictException("Contracts have values for this field. Deactivate it instead.", ProblemTypesV3.InUse);
        }
        _db.CustomFieldDefinitions.Remove(definition);
        _audit.Record("settings.custom-fields", "Deleted custom field " + definition.Label + " (" + definition.Key + ")");
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>PUT admin/custom-fields/order: order = index in ids; others keep their relative order after them.</summary>
    public async Task<List<CustomFieldDefDto>> ReorderAsync(IdsInput input, CancellationToken cancellationToken = default)
    {
        var ids = (input?.Ids ?? new List<Guid>()).Distinct().ToList();
        var all = await _db.CustomFieldDefinitions.ToListAsync(cancellationToken);
        if (ids.Any(id => all.All(d => d.Id != id)))
        {
            throw new InputValidationException("ids", "Unknown custom field.");
        }
        var rest = Ordered(all.Where(d => !ids.Contains(d.Id))).ToList();
        var order = 0;
        foreach (var id in ids)
        {
            all.First(d => d.Id == id).SortOrder = order++;
        }
        foreach (var definition in rest)
        {
            definition.SortOrder = order++;
        }
        _audit.Record("settings.custom-fields", "Reordered custom fields");
        await _db.SaveChangesAsync(cancellationToken);
        return Ordered(all).Select(ToDto).ToList();
    }

    // ---------- values: parsing ----------

    private static bool TryParseNumber(string text, out double number) =>
        double.TryParse(text.Trim().Replace(" ", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    /// <summary>Lenient parsing from text (imports, sync sources). Returns an error message or null.</summary>
    public static string? ParseText(CustomFieldDefinition definition, string? text, string? contractCurrency, out ParsedCustomValue parsed)
    {
        parsed = new ParsedCustomValue(definition);
        if (string.IsNullOrWhiteSpace(text))
        {
            parsed.IsEmpty = true;
            return null;
        }
        var value = text.Trim();
        switch (definition.FieldType)
        {
            case CustomFieldType.Text:
                if (value.Length > 1000) return "At most 1000 characters.";
                parsed.TextValue = value;
                return null;
            case CustomFieldType.Number:
                if (!TryParseNumber(value, out var number)) return "Enter a number.";
                parsed.NumberValue = number;
                return null;
            case CustomFieldType.Money:
            {
                var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var currency = contractCurrency;
                var amountText = value;
                if (parts.Length == 2 && parts[1].Length == 3 && !TryParseNumber(parts[1], out _))
                {
                    amountText = parts[0];
                    currency = parts[1].ToUpperInvariant();
                }
                if (!TryParseNumber(amountText, out var amount)) return "Enter an amount, optionally followed by a currency code.";
                parsed.NumberValue = amount;
                parsed.TextValue = string.IsNullOrWhiteSpace(currency) ? null : currency!.Trim().ToUpperInvariant();
                return null;
            }
            case CustomFieldType.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return "Enter a date as yyyy-MM-dd.";
                parsed.DateValue = date;
                return null;
            case CustomFieldType.YesNo:
            {
                var lower = value.ToLowerInvariant();
                if (lower is "true" or "yes" or "1" or "ja" or "x") parsed.BoolValue = true;
                else if (lower is "false" or "no" or "0" or "nej") parsed.BoolValue = false;
                else return "Enter yes or no.";
                return null;
            }
            case CustomFieldType.Choice:
            {
                var match = ReadOptions(definition).FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                if (match is null) return "Choose one of: " + string.Join(", ", ReadOptions(definition)) + ".";
                parsed.TextValue = match;
                return null;
            }
            case CustomFieldType.MultiChoice:
            {
                var options = ReadOptions(definition);
                var chosen = new List<string>();
                foreach (var part in value.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var match = options.FirstOrDefault(o => string.Equals(o, part, StringComparison.OrdinalIgnoreCase));
                    if (match is null) return "Unknown option '" + part + "'.";
                    if (!chosen.Contains(match)) chosen.Add(match);
                }
                if (chosen.Count == 0)
                {
                    parsed.IsEmpty = true;
                    return null;
                }
                parsed.TextValue = "|" + string.Join("|", chosen) + "|";
                return null;
            }
            case CustomFieldType.Person:
                if (!Guid.TryParse(value, out var userId)) return "Choose a person.";
                parsed.UserValue = userId;
                return null;
        }
        return "Unsupported field type.";
    }

    /// <summary>Strict parsing of a JSON value (contract body): the JSON type must match the field type.</summary>
    public static string? ParseJson(CustomFieldDefinition definition, JsonElement element, IReadOnlyCollection<string> activeCurrencies, out ParsedCustomValue parsed)
    {
        parsed = new ParsedCustomValue(definition);
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            parsed.IsEmpty = true;
            return null;
        }
        switch (definition.FieldType)
        {
            case CustomFieldType.Text:
            case CustomFieldType.Choice:
            case CustomFieldType.Date:
            case CustomFieldType.Person:
            {
                if (element.ValueKind != JsonValueKind.String) return "Expected text.";
                var text = element.GetString() ?? "";
                if (text.Length == 0)
                {
                    parsed.IsEmpty = true;
                    return null;
                }
                if (definition.FieldType == CustomFieldType.Text)
                {
                    if (text.Length > 1000) return "At most 1000 characters.";
                    parsed.TextValue = text.Trim();
                    return null;
                }
                if (definition.FieldType == CustomFieldType.Choice)
                {
                    if (!ReadOptions(definition).Contains(text)) return "Choose one of: " + string.Join(", ", ReadOptions(definition)) + ".";
                    parsed.TextValue = text;
                    return null;
                }
                if (definition.FieldType == CustomFieldType.Date)
                {
                    if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return "Expected a date as yyyy-MM-dd.";
                    parsed.DateValue = date;
                    return null;
                }
                if (!Guid.TryParse(text, out var userId)) return "Expected a user id.";
                parsed.UserValue = userId;
                return null;
            }
            case CustomFieldType.Number:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number)) return "Expected a number.";
                parsed.NumberValue = number;
                return null;
            case CustomFieldType.Money:
            {
                if (element.ValueKind != JsonValueKind.Object) return "Expected { amount, currency }.";
                double? amount = null;
                string? currency = null;
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("amount") && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value)) amount = value;
                    else if (property.NameEquals("currency") && property.Value.ValueKind == JsonValueKind.String) currency = property.Value.GetString();
                }
                if (amount is null) return "Expected an amount.";
                if (string.IsNullOrWhiteSpace(currency) || !activeCurrencies.Contains(currency.Trim().ToUpperInvariant())) return "Expected an active currency code.";
                parsed.NumberValue = amount;
                parsed.TextValue = currency.Trim().ToUpperInvariant();
                return null;
            }
            case CustomFieldType.YesNo:
                if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False) return "Expected true or false.";
                parsed.BoolValue = element.ValueKind == JsonValueKind.True;
                return null;
            case CustomFieldType.MultiChoice:
            {
                if (element.ValueKind != JsonValueKind.Array) return "Expected a list of options.";
                var options = ReadOptions(definition);
                var chosen = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) return "Expected a list of options.";
                    var option = item.GetString() ?? "";
                    if (!options.Contains(option)) return "Unknown option '" + option + "'.";
                    if (!chosen.Contains(option)) chosen.Add(option);
                }
                if (chosen.Count == 0)
                {
                    parsed.IsEmpty = true;
                    return null;
                }
                parsed.TextValue = "|" + string.Join("|", chosen) + "|";
                return null;
            }
        }
        return "Unsupported field type.";
    }

    /// <summary>
    /// Validates the customFields of a contract body (CONTRACT_V3 section 1.3) and returns the full set
    /// of values to store: every active field that applies to the type, empty when not sent.
    /// Required fields are only checked when <paramref name="checkRequired"/> (create, Draft update).
    /// </summary>
    public async Task<List<ParsedCustomValue>> ValidateAsync(
        Dictionary<string, JsonElement> input, Guid typeId, Dictionary<string, string[]> errors, bool checkRequired,
        CancellationToken cancellationToken = default)
    {
        var all = await _db.CustomFieldDefinitions.AsNoTracking().ToListAsync(cancellationToken);
        var currencies = await CurrencyTable.ActiveCodesAsync(_db, cancellationToken);
        var result = new List<ParsedCustomValue>();
        foreach (var pair in input)
        {
            var definition = all.FirstOrDefault(d => d.Key == pair.Key);
            var field = "customFields." + pair.Key;
            if (definition is null)
            {
                ContractValidator.Add(errors, field, "Unknown custom field.");
                continue;
            }
            if (!definition.IsActive)
            {
                ContractValidator.Add(errors, field, definition.Label + " is no longer in use.");
                continue;
            }
            if (!ReferenceData.Applies(definition, typeId))
            {
                ContractValidator.Add(errors, field, definition.Label + " does not apply to this contract type.");
                continue;
            }
            var error = ParseJson(definition, pair.Value, currencies, out var parsed);
            if (error is not null)
            {
                ContractValidator.Add(errors, field, definition.Label + ": " + error);
                continue;
            }
            if (parsed.UserValue is not null)
            {
                var userId = parsed.UserValue.Value;
                if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
                {
                    ContractValidator.Add(errors, field, definition.Label + ": unknown user.");
                    continue;
                }
            }
            result.Add(parsed);
        }
        // Keys not given are cleared: every other applicable field is stored as empty.
        foreach (var definition in all.Where(d => d.IsActive && ReferenceData.Applies(d, typeId)))
        {
            if (!input.ContainsKey(definition.Key))
            {
                result.Add(new ParsedCustomValue(definition) { IsEmpty = true });
            }
        }
        if (checkRequired)
        {
            foreach (var definition in all.Where(d => d.IsActive && d.Required && ReferenceData.Applies(d, typeId)))
            {
                var value = result.FirstOrDefault(r => r.Definition.Id == definition.Id);
                var sentBad = input.ContainsKey(definition.Key) && value is null;
                if (!sentBad && (value is null || value.IsEmpty))
                {
                    ContractValidator.Add(errors, "customFields." + definition.Key, definition.Label + " is required.");
                }
            }
        }
        return result;
    }

    /// <summary>Required custom fields missing on a stored contract (used at submit).</summary>
    public async Task CheckStoredRequiredAsync(Contract contract, Dictionary<string, string[]> errors, CancellationToken cancellationToken = default)
    {
        var required = (await _db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive && d.Required).ToListAsync(cancellationToken))
            .Where(d => ReferenceData.Applies(d, contract.TypeId))
            .ToList();
        if (required.Count == 0)
        {
            return;
        }
        var contractId = contract.Id;
        var present = (await _db.CustomFieldValues.AsNoTracking().Where(v => v.ContractId == contractId).Select(v => v.FieldId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var definition in required)
        {
            if (!present.Contains(definition.Id))
            {
                ContractValidator.Add(errors, "customFields." + definition.Key, definition.Label + " is required.");
            }
        }
    }

    private static string DisplayOf(CustomFieldDefinition definition, CustomFieldValue? row) =>
        row is null ? "(empty)" : DisplayRaw(RawValue(definition, row), null);

    /// <summary>
    /// Adds, changes or removes value rows of a contract (unit of work). Returns the changes as
    /// (field "cf.&lt;key&gt;", label, old, new) for the audit trail and the version comparison.
    /// </summary>
    public async Task<List<FieldChange>> SaveValuesAsync(Guid contractId, IEnumerable<ParsedCustomValue> values, bool isNewContract, CancellationToken cancellationToken = default)
    {
        var changed = new List<FieldChange>();
        var existing = isNewContract
            ? new List<CustomFieldValue>()
            : await _db.CustomFieldValues.Where(v => v.ContractId == contractId).ToListAsync(cancellationToken);
        foreach (var value in values)
        {
            var definition = value.Definition;
            var field = "cf." + definition.Key;
            var row = existing.FirstOrDefault(v => v.FieldId == definition.Id);
            var oldText = DisplayOf(definition, row);
            if (value.IsEmpty)
            {
                if (row is not null)
                {
                    _db.CustomFieldValues.Remove(row);
                    existing.Remove(row);
                    changed.Add(new FieldChange(field, definition.Label, oldText, "(empty)"));
                }
                continue;
            }
            var isNew = row is null;
            if (row is null)
            {
                row = new CustomFieldValue { Id = Guid.NewGuid(), ContractId = contractId, FieldId = definition.Id };
                _db.CustomFieldValues.Add(row);
                existing.Add(row);
            }
            var same = !isNew && row.TextValue == value.TextValue && row.NumberValue == value.NumberValue && row.DateValue == value.DateValue
                && row.BoolValue == value.BoolValue && row.UserValue == value.UserValue;
            row.TextValue = value.TextValue;
            row.NumberValue = value.NumberValue;
            row.DateValue = value.DateValue;
            row.BoolValue = value.BoolValue;
            row.UserValue = value.UserValue;
            if (!same)
            {
                changed.Add(new FieldChange(field, definition.Label, oldText, DisplayOf(definition, row)));
            }
        }
        return changed;
    }

    // ---------- values: output ----------

    /// <summary>The API value: string, number, boolean, string[], { amount, currency } or null.</summary>
    public static object? RawValue(CustomFieldDefinition definition, CustomFieldValue value)
    {
        switch (definition.FieldType)
        {
            case CustomFieldType.Number:
                return value.NumberValue;
            case CustomFieldType.Money:
                return value.NumberValue is null ? null : new Dictionary<string, object?> { ["amount"] = value.NumberValue, ["currency"] = value.TextValue };
            case CustomFieldType.Date:
                return value.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case CustomFieldType.YesNo:
                return value.BoolValue;
            case CustomFieldType.MultiChoice:
                return (value.TextValue ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            case CustomFieldType.Person:
                return value.UserValue?.ToString();
            default:
                return value.TextValue;
        }
    }

    /// <summary>Display text of a raw value (CSV, version comparison).</summary>
    public static string DisplayRaw(object? value, ReferenceData? reference)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (value)
        {
            case null:
                return "";
            case bool flag:
                return flag ? "Yes" : "No";
            case double number:
                return number.ToString("0.##", culture);
            case List<string> list:
                return string.Join("; ", list);
            case Dictionary<string, object?> money:
            {
                var amount = money.TryGetValue("amount", out var a) && a is double d ? d.ToString("0.##", culture) : "";
                var currency = money.TryGetValue("currency", out var c) ? c as string : null;
                return string.IsNullOrEmpty(currency) ? amount : amount + " " + currency;
            }
            case string text:
                if (reference is not null && Guid.TryParse(text, out var userId) && reference.Users.ContainsKey(userId))
                {
                    return reference.UserName(userId);
                }
                return text;
            default:
                return Convert.ToString(value, culture) ?? "";
        }
    }

    // ---------- field rules ----------

    public async Task<FieldRulesDto> GetFieldRulesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.FieldRules.AsNoTracking().ToListAsync(cancellationToken);
        var result = new FieldRulesDto();
        foreach (var group in rows.GroupBy(r => r.ContractTypeId))
        {
            var rules = new TypeFieldRulesDto { TypeId = group.Key };
            foreach (var row in group)
            {
                if (row.Required && OptionalStandardFields.Contains(row.Field))
                {
                    rules.Required.Add(row.Field);
                }
                if (row.DefaultValue is null)
                {
                    continue;
                }
                switch (row.Field)
                {
                    case "currency": rules.Defaults.Currency = row.DefaultValue; break;
                    case "noticePeriodDays":
                        if (int.TryParse(row.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var notice)) rules.Defaults.NoticePeriodDays = notice;
                        break;
                    case "reminderRecipient": rules.Defaults.ReminderRecipient = row.DefaultValue; break;
                    case "autoRenew":
                        if (bool.TryParse(row.DefaultValue, out var autoRenew)) rules.Defaults.AutoRenew = autoRenew;
                        break;
                    case "renewalMonths":
                        if (int.TryParse(row.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var months)) rules.Defaults.RenewalMonths = months;
                        break;
                }
            }
            rules.Required = OptionalStandardFields.Where(f => rules.Required.Contains(f)).ToList();
            result.Types.Add(rules);
        }
        return result;
    }

    public async Task<FieldRulesDto> GetFieldRulesForUserAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        return await GetFieldRulesAsync(cancellationToken);
    }

    /// <summary>PUT admin/field-rules: replaces the whole object.</summary>
    public async Task<FieldRulesDto> ReplaceFieldRulesAsync(FieldRulesDto input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        var types = input?.Types ?? new List<TypeFieldRulesDto>();
        var typeIds = (await _db.ContractTypes.AsNoTracking().Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        var currencies = await CurrencyTable.ActiveCodesAsync(_db, cancellationToken);
        var seen = new HashSet<Guid>();
        foreach (var rules in types.Where(t => t is not null))
        {
            if (!typeIds.Contains(rules.TypeId)) ContractValidator.Add(errors, "types", "Unknown contract type.");
            if (!seen.Add(rules.TypeId)) ContractValidator.Add(errors, "types", "A contract type can appear only once.");
            foreach (var field in rules.Required ?? new List<string>())
            {
                if (!OptionalStandardFields.Contains(field)) ContractValidator.Add(errors, "types", "Unknown required field: " + field + ".");
            }
            var defaults = rules.Defaults ?? new FieldRuleDefaultsDto();
            if (defaults.Currency is not null && !currencies.Contains(defaults.Currency)) ContractValidator.Add(errors, "types", "Default currency must be an active currency.");
            if (defaults.NoticePeriodDays is not null && (defaults.NoticePeriodDays < 0 || defaults.NoticePeriodDays > 3650)) ContractValidator.Add(errors, "types", "Default notice period must be 0-3650 days.");
            if (defaults.RenewalMonths is not null && (defaults.RenewalMonths < 1 || defaults.RenewalMonths > 120)) ContractValidator.Add(errors, "types", "Default renewal period must be 1-120 months.");
            if (defaults.ReminderRecipient is not null && !Enum.TryParse<ReminderRecipient>(defaults.ReminderRecipient, false, out _)) ContractValidator.Add(errors, "types", "Default reminder recipient must be Owner, Delegate or Responsible.");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        _db.FieldRules.RemoveRange(await _db.FieldRules.ToListAsync(cancellationToken));
        foreach (var rules in types)
        {
            foreach (var field in (rules.Required ?? new List<string>()).Distinct())
            {
                _db.FieldRules.Add(new ContractTypeFieldRule { Id = Guid.NewGuid(), ContractTypeId = rules.TypeId, Field = field, Required = true });
            }
            var defaults = rules.Defaults ?? new FieldRuleDefaultsDto();
            void AddDefault(string field, string? value)
            {
                if (value is not null)
                {
                    _db.FieldRules.Add(new ContractTypeFieldRule { Id = Guid.NewGuid(), ContractTypeId = rules.TypeId, Field = field, DefaultValue = value });
                }
            }
            AddDefault("currency", defaults.Currency);
            AddDefault("noticePeriodDays", defaults.NoticePeriodDays?.ToString(CultureInfo.InvariantCulture));
            AddDefault("reminderRecipient", defaults.ReminderRecipient);
            AddDefault("autoRenew", defaults.AutoRenew is null ? null : (defaults.AutoRenew.Value ? "true" : "false"));
            AddDefault("renewalMonths", defaults.RenewalMonths?.ToString(CultureInfo.InvariantCulture));
        }
        _audit.Record("settings.field-rules", "Updated field rules (" + types.Count + " contract types)");
        await _db.SaveChangesAsync(cancellationToken);
        return await GetFieldRulesAsync(cancellationToken);
    }

    /// <summary>Whether an OPTIONAL_STANDARD_FIELDS field is empty in a contract body.</summary>
    public static bool IsMissing(ContractInput input, string field)
    {
        switch (field)
        {
            case "delegateId": return input.DelegateId is null;
            case "responsibleId": return input.ResponsibleId is null;
            case "endDate": return input.EndDate is null || input.OpenEnded == true;
            case "annualValue": return input.AnnualValue is null;
            case "totalValue": return input.TotalValue is null;
            case "paymentTerms": return string.IsNullOrWhiteSpace(input.PaymentTerms);
            case "priceAdjustment": return string.IsNullOrWhiteSpace(input.PriceAdjustment);
            case "governingLaw": return string.IsNullOrWhiteSpace(input.GoverningLaw);
            case "liabilityCap": return string.IsNullOrWhiteSpace(input.LiabilityCap);
            case "exclusivity": return string.IsNullOrWhiteSpace(input.Exclusivity);
            case "summary": return string.IsNullOrWhiteSpace(input.Summary);
        }
        return false;
    }

    /// <summary>Adds a 400 error for every field the type requires and the body leaves empty.</summary>
    public async Task CheckRequiredRulesAsync(ContractInput input, Dictionary<string, string[]> errors, CancellationToken cancellationToken = default)
    {
        if (input.TypeId is null)
        {
            return;
        }
        var typeId = input.TypeId.Value;
        var rules = await _db.FieldRules.AsNoTracking().Where(r => r.ContractTypeId == typeId && r.Required).ToListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            if (OptionalStandardFields.Contains(rule.Field) && IsMissing(input, rule.Field))
            {
                ContractValidator.Add(errors, rule.Field, rule.Field == "endDate"
                    ? "This contract type needs an end date (it cannot be open-ended)."
                    : "Required for this contract type.");
            }
        }
    }

    /// <summary>Import: the type's defaults for empty currency, reminderRecipient, autoRenew, renewalMonths cells.</summary>
    public static Dictionary<string, string> DefaultsFor(IEnumerable<ContractTypeFieldRule> rules, Guid typeId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(r => r.ContractTypeId == typeId && r.DefaultValue != null))
        {
            result[rule.Field] = rule.DefaultValue!;
        }
        return result;
    }
}
