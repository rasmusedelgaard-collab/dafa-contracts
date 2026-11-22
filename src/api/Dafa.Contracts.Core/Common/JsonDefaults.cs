using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dafa.Contracts.Core.Common;

/// <summary>Serializer settings for JSON stored in database columns (rule stages, deviations).</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static List<T> ReadList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<T>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>();
        }
        catch (JsonException)
        {
            return new List<T>();
        }
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
