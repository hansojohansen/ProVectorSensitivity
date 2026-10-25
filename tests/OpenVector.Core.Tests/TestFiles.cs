using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace OpenVector.Core.Tests;

internal static class TestFiles
{
    public static string PathOf(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);
    public static string Read(params string[] parts) => File.ReadAllText(PathOf(parts));
    public static JsonNode Golden(string name) => JsonNode.Parse(Read("Golden", name))!;

    private static readonly Dictionary<string, JsonSchema> Schemas = new();
    private static readonly Lock Gate = new();

    /// <summary>Schema by name ("profile", "movement-trial", "session"); built once (the registry is global).</summary>
    public static JsonSchema Schema(string name)
    {
        lock (Gate)
        {
            if (!Schemas.TryGetValue(name, out var s))
                Schemas[name] = s = JsonSchema.FromText(Read("schemas", $"{name}.schema.json"));
            return s;
        }
    }

    public static void AssertValid(string schema, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = Schema(schema).Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!res.IsValid)
        {
            var msgs = (res.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}"));
            Assert.Fail($"{schema} validation failed:\n" + string.Join("\n", msgs.Take(20)));
        }
    }

    /// <summary>Remove null-valued properties recursively (serializer omits optional nulls).</summary>
    public static JsonNode? StripNulls(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var key in o.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) o.Remove(key);
                foreach (var kv in o.ToList()) StripNulls(kv.Value);
                break;
            case JsonArray a:
                foreach (var item in a) StripNulls(item);
                break;
        }
        return n;
    }

    /// <summary>JSON paths where two documents differ (for readable assertion messages).</summary>
    public static IEnumerable<string> Diff(JsonNode? a, JsonNode? b, string path = "$")
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(kv => kv.Key).Union(ob.Select(kv => kv.Key)))
                foreach (var d in Diff(oa[key], ob[key], $"{path}.{key}")) yield return d;
        }
        else if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
        {
            for (int i = 0; i < aa.Count; i++)
                foreach (var d in Diff(aa[i], ab[i], $"{path}[{i}]")) yield return d;
        }
        else if (!JsonNode.DeepEquals(a, b))
            yield return $"{path}: {a?.ToJsonString()} != {b?.ToJsonString()}";
    }

    public static double[] Doubles(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
}
