using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVector.Core.Json;

/// <summary>System.Text.Json settings for all OpenVector schemas (snake_case, string enums, nulls omitted
/// unless a property is marked <c>[JsonIgnore(Condition = Never)]</c> because the schema requires it).</summary>
public static class OpenVectorJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = indented,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"null {typeof(T).Name}");

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Options);

    /// <summary>Read a JSON Lines file of MovementTrial (or any T) records.</summary>
    public static IEnumerable<T> ReadJsonLines<T>(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (!string.IsNullOrWhiteSpace(line)) yield return Deserialize<T>(line);
    }

    public static void WriteJsonLines<T>(TextWriter writer, IEnumerable<T> items)
    {
        foreach (var item in items) writer.WriteLine(Serialize(item));
    }
}
