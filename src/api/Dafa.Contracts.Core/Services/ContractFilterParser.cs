using System.Globalization;
using System.Text.Json;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Dtos;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Builds a ContractFilter from a query string (repeated parameters, cf.&lt;key&gt;) or from a JSON
/// object (data room: scalars or arrays). Malformed ids/dates/numbers/booleans are 400 on the
/// parameter; unknown but well-formed ids simply match nothing.
/// </summary>
public static class ContractFilterParser
{
    private static readonly string[] ListKeys = { "id", "entityId", "departmentId", "typeId", "status", "counterpartyId", "ownerId", "valueBand", "tagId", "columns" };

    public static ContractFilter FromQuery(IEnumerable<KeyValuePair<string, string[]>> query)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query)
        {
            if (!values.TryGetValue(pair.Key, out var list))
            {
                list = new List<string>();
                values[pair.Key] = list;
            }
            foreach (var value in pair.Value)
            {
                if (value is not null)
                {
                    list.Add(value);
                }
            }
        }
        return Build(values);
    }

    public static ContractFilter FromJson(JsonElement? element)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (element is null || element.Value.ValueKind == JsonValueKind.Null || element.Value.ValueKind == JsonValueKind.Undefined)
        {
            return Build(values);
        }
        if (element.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InputValidationException("filter", "The filter must be an object.");
        }
        foreach (var property in element.Value.EnumerateObject())
        {
            if (property.NameEquals("cf") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var custom in property.Value.EnumerateObject())
                {
                    values["cf." + custom.Name] = new List<string> { Scalar(custom.Value) ?? "" };
                }
                continue;
            }
            var list = new List<string>();
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    var text = Scalar(item);
                    if (text is not null) list.Add(text);
                }
            }
            else
            {
                var text = Scalar(property.Value);
                if (text is not null) list.Add(text);
            }
            values[property.Name] = list;
        }
        return Build(values);
    }

    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static List<Guid> Ids(Dictionary<string, List<string>> values, string key)
    {
        var result = new List<Guid>();
        if (!values.TryGetValue(key, out var list))
        {
            return result;
        }
        foreach (var raw in list.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Guid.TryParse(raw, out var id))
            {
                throw new InputValidationException(key, "Not a valid id.");
            }
            result.Add(id);
        }
        return result;
    }

    private static string? One(Dictionary<string, List<string>> values, string key) =>
        values.TryGetValue(key, out var list) ? list.LastOrDefault(v => !string.IsNullOrWhiteSpace(v)) : null;

    private static bool? Bool(Dictionary<string, List<string>> values, string key)
    {
        var text = One(values, key);
        if (text is null) return null;
        if (bool.TryParse(text.Trim(), out var value)) return value;
        throw new InputValidationException(key, "Use true or false.");
    }

    private static int? Int(Dictionary<string, List<string>> values, string key)
    {
        var text = One(values, key);
        if (text is null) return null;
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        throw new InputValidationException(key, "Use a whole number.");
    }

    private static DateOnly? Date(Dictionary<string, List<string>> values, string key)
    {
        var text = One(values, key);
        if (text is null) return null;
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) return value;
        throw new InputValidationException(key, "Use a date as yyyy-MM-dd.");
    }

    private static ContractFilter Build(Dictionary<string, List<string>> values)
    {
        var filter = new ContractFilter
        {
            Q = One(values, "q"),
            Ids = Ids(values, "id"),
            EntityIds = Ids(values, "entityId"),
            DepartmentIds = Ids(values, "departmentId"),
            TypeIds = Ids(values, "typeId"),
            CounterpartyIds = Ids(values, "counterpartyId"),
            OwnerIds = Ids(values, "ownerId"),
            TagIds = Ids(values, "tagId"),
            DateField = One(values, "dateField"),
            From = Date(values, "from"),
            To = Date(values, "to"),
            NoticeWithinDays = Int(values, "noticeWithinDays"),
            Decided = Bool(values, "decided"),
            ChangeOfControl = Bool(values, "changeOfControl"),
            Exclusivity = Bool(values, "exclusivity"),
            AutoRenew = Bool(values, "autoRenew"),
            MissingData = Bool(values, "missingData"),
            MissingField = One(values, "missingField"),
            MissingSignedDocument = Bool(values, "missingSignedDocument"),
            Watching = Bool(values, "watching"),
            IncludeEnded = Bool(values, "includeEnded"),
            View = One(values, "view"),
            Sort = One(values, "sort"),
            Page = Int(values, "page"),
            PageSize = Int(values, "pageSize")
        };
        var person = Ids(values, "personId");
        filter.PersonId = person.Count > 0 ? person[0] : null;
        if (values.TryGetValue("status", out var statuses))
        {
            filter.Statuses = statuses.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        if (values.TryGetValue("valueBand", out var bands))
        {
            foreach (var band in bands.SelectMany(b => b.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (!ContractFilterEngine.ValueBandKeys.Contains(band))
                {
                    throw new InputValidationException("valueBand", "Use lt1m, 1to5m, 5to20m or gte20m.");
                }
                filter.ValueBands.Add(band);
            }
        }
        var min = One(values, "minValueDkk");
        if (min is not null)
        {
            if (!decimal.TryParse(min.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var minValue))
            {
                throw new InputValidationException("minValueDkk", "Use a number.");
            }
            filter.MinValueDkk = minValue;
        }
        if (filter.DateField is not null && !ContractFilterEngine.DateFields.Contains(filter.DateField))
        {
            throw new InputValidationException("dateField", "Use notice, end, start, created or signed.");
        }
        if (values.TryGetValue("columns", out var columns))
        {
            filter.Columns = columns.SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        }
        foreach (var pair in values)
        {
            if (pair.Key.StartsWith("cf.", StringComparison.OrdinalIgnoreCase) && pair.Key.Length > 3)
            {
                var value = pair.Value.LastOrDefault();
                if (value is not null)
                {
                    filter.Custom[pair.Key.Substring(3)] = value;
                }
            }
        }
        // The release 1 single-value parameters keep working: they are covered by the lists above.
        _ = ListKeys;
        return filter;
    }
}
