using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenVector.RawAccel.Model;

namespace OpenVector.RawAccel.Settings;

public sealed class UnsupportedRawAccelVersionException(string? version)
    : Exception($"Raw Accel settings version '{version ?? "(missing)"}' is not supported. Tested: {string.Join(", ", RawAccelKeyMap.Supported.Keys)}.")
{
    public string? Version { get; } = version;
}

/// <summary>
/// A Raw Accel settings.json held as a mutable JSON tree. Every key, unknown or not, round-trips untouched;
/// only <see cref="WriteOwned"/> changes values, and only the owned fields of one profile.
/// </summary>
public sealed class RawAccelSettingsDocument
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonObject Root { get; }
    public RawAccelKeyMap Keys { get; }

    private RawAccelSettingsDocument(JsonObject root, RawAccelKeyMap keys) { Root = root; Keys = keys; }

    /// <summary>Parses settings.json; throws <see cref="UnsupportedRawAccelVersionException"/> for untested versions.</summary>
    public static RawAccelSettingsDocument Parse(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow })
                   as JsonObject ?? throw new JsonException("settings.json root is not an object.");
        string? version = node["version"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var keys = RawAccelKeyMap.ForVersion(version) ?? throw new UnsupportedRawAccelVersionException(version);
        return new RawAccelSettingsDocument(node, keys);
    }

    public static RawAccelSettingsDocument Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Reads only the version string, without validating; null if missing or unparsable.</summary>
    public static string? PeekVersion(string json)
    {
        try { return (JsonNode.Parse(json) as JsonObject)?["version"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public RawAccelSettingsDocument Clone() => new((JsonObject)Root.DeepClone(), Keys);

    public string ToJson() => Root.ToJsonString(WriteOptions);
    public byte[] ToUtf8Bytes() => new UTF8Encoding(false).GetBytes(ToJson());
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public IReadOnlyList<string> ProfileNames =>
        (Root[Keys.Profiles] as JsonArray)?.Select(p => p?[Keys.Name]?.GetValue<string>() ?? "").ToList() ?? new List<string>();

    /// <summary>The selected profile: by name, or the first (default) profile when name is null.</summary>
    public JsonObject GetProfileNode(string? name)
    {
        var arr = Root[Keys.Profiles] as JsonArray ?? throw new InvalidDataException("settings.json has no profiles array.");
        if (arr.Count == 0) throw new InvalidDataException("settings.json has no profiles. Open the Raw Accel GUI once to create one.");
        if (name is null) return arr[0] as JsonObject ?? throw new InvalidDataException("profile 0 is not an object.");
        var matches = arr.OfType<JsonObject>().Where(p => p[Keys.Name] is JsonValue v && v.TryGetValue<string>(out var s) && s == name).ToList();
        if (matches.Count == 0) throw new KeyNotFoundException($"Raw Accel profile '{name}' not found.");
        if (matches.Count > 1) throw new InvalidDataException($"Raw Accel profile name '{name}' is not unique.");
        return matches[0];
    }

    public string ResolveProfileName(string? name) => GetProfileNode(name)[Keys.Name]!.GetValue<string>();

    /// <summary>Device DPI that applies to devices using the default config (0 = unset).</summary>
    public int DefaultDeviceDpi => (Root[Keys.DefaultDeviceConfig] as JsonObject)?[Keys.Dpi] is JsonValue v && v.TryGetValue<int>(out var d) ? d : 0;

    // ---- reading ----

    public OwnedProfileValues ReadOwned(string? profileName)
    {
        var p = GetProfileNode(profileName);
        var range = Obj(p, Keys.RangeWeights);
        return new OwnedProfileValues
        {
            OutputDpi = Num(p, Keys.OutputDpi),
            YxRatio = Num(p, Keys.YxRatio),
            LrRatio = Num(p, Keys.LrRatio),
            UdRatio = Num(p, Keys.UdRatio),
            RangeWeightX = Num(range, Keys.X),
            RangeWeightY = Num(range, Keys.Y),
            Whole = Bool(Obj(p, Keys.SpeedArgs), Keys.Whole),
            AccelX = ReadCurve(Obj(p, Keys.AccelX)),
        };
    }

    /// <summary>The profile as a <see cref="RawAccelProfile"/> for <see cref="RawAccelModel"/>.</summary>
    public RawAccelProfile ReadProfile(string? profileName)
    {
        var p = GetProfileNode(profileName);
        var range = Obj(p, Keys.RangeWeights); var domain = Obj(p, Keys.DomainWeights); var speed = Obj(p, Keys.SpeedArgs);
        return new RawAccelProfile
        {
            OutputDpi = Num(p, Keys.OutputDpi),
            YxOutputDpiRatio = Num(p, Keys.YxRatio),
            LrOutputDpiRatio = Num(p, Keys.LrRatio),
            UdOutputDpiRatio = Num(p, Keys.UdRatio),
            DegreesRotation = Num(p, Keys.Rotation),
            DegreesSnap = Num(p, Keys.Snap),
            SpeedMax = Num(p, Keys.SpeedCap),
            RangeWeightX = Num(range, Keys.X), RangeWeightY = Num(range, Keys.Y),
            DomainWeightX = Num(domain, Keys.X), DomainWeightY = Num(domain, Keys.Y),
            Whole = Bool(speed, Keys.Whole),
            LpNorm = Num(speed, Keys.LpNorm),
            AccelX = ReadCurve(Obj(p, Keys.AccelX)),
            AccelY = ReadCurve(Obj(p, Keys.AccelY)),
        };
    }

    /// <summary>True if any EMA smoothing half-life of the profile is non-zero (not modelled).</summary>
    public bool HasSmoothing(string? profileName)
    {
        var speed = Obj(GetProfileNode(profileName), Keys.SpeedArgs);
        return Num(speed, Keys.InputSmoothHalflife) != 0 || Num(speed, Keys.ScaleSmoothHalflife) != 0 || Num(speed, Keys.OutputSmoothHalflife) != 0;
    }

    public AccelCurve ReadCurve(JsonObject a)
    {
        string mode = a[Keys.Mode]?.GetValue<string>() ?? throw new InvalidDataException($"accel '{Keys.Mode}' missing");
        var data = (a[Keys.Data] as JsonArray)?.Select(n => (float)n!.GetValue<double>()).ToArray() ?? Array.Empty<float>();
        return new AccelCurve { Mode = ParseMode(mode), Gain = Bool(a, Keys.Gain), Data = data };
    }

    private static AccelMode ParseMode(string s) => s switch
    {
        "classic" => AccelMode.Classic, "jump" => AccelMode.Jump, "natural" => AccelMode.Natural,
        "synchronous" => AccelMode.Synchronous, "power" => AccelMode.Power, "lut" => AccelMode.Lut, "noaccel" => AccelMode.NoAccel,
        _ => throw new InvalidDataException($"unknown accel mode '{s}'"),
    };

    private static string ModeString(AccelMode m) => m switch
    {
        AccelMode.Classic => "classic", AccelMode.Jump => "jump", AccelMode.Natural => "natural",
        AccelMode.Synchronous => "synchronous", AccelMode.Power => "power", AccelMode.Lut => "lut", _ => "noaccel",
    };

    // ---- writing ----

    /// <summary>
    /// Writes the owned fields into the selected profile. Keys are replaced in place (order preserved); the
    /// accel curve object is only touched when <paramref name="values"/>.AccelX differs from what is there, and
    /// then only its "mode", "Gain / Velocity" and "data" keys change.
    /// </summary>
    public void WriteOwned(string? profileName, OwnedProfileValues values)
    {
        var p = GetProfileNode(profileName);
        SetNum(p, Keys.OutputDpi, values.OutputDpi);
        SetNum(p, Keys.YxRatio, values.YxRatio);
        SetNum(p, Keys.LrRatio, values.LrRatio);
        SetNum(p, Keys.UdRatio, values.UdRatio);
        var range = Obj(p, Keys.RangeWeights);
        SetNum(range, Keys.X, values.RangeWeightX);
        SetNum(range, Keys.Y, values.RangeWeightY);
        var speed = Obj(p, Keys.SpeedArgs);
        if (Bool(speed, Keys.Whole) != values.Whole) speed[Keys.Whole] = JsonValue.Create(values.Whole);
        var ax = Obj(p, Keys.AccelX);
        var current = ReadCurve(ax);
        bool same = current.Mode == values.AccelX.Mode && current.Gain == values.AccelX.Gain && current.Data.SequenceEqual(values.AccelX.Data);
        if (!same)
        {
            ax[Keys.Mode] = JsonValue.Create(ModeString(values.AccelX.Mode));
            ax[Keys.Gain] = JsonValue.Create(values.AccelX.Gain);
            var arr = new JsonArray();
            // shortest float text as a double node ("1.05", not 1.0499999523...); reads back to the same float
            foreach (var f in values.AccelX.Data) arr.Add(JsonValue.Create(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)));
            ax[Keys.Data] = arr;
        }
    }

    private static void SetNum(JsonObject o, string key, double v)
    {
        if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(key, "value must be finite");
        if (o[key] is JsonValue old && old.TryGetValue<double>(out var d) && d == v) return; // keep original text
        o[key] = JsonValue.Create(v);
    }

    // ---- helpers ----
    internal JsonObject Obj(JsonObject o, string key) => o[key] as JsonObject ?? throw new InvalidDataException($"'{key}' missing or not an object");
    internal static double Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : throw new InvalidDataException($"'{key}' missing or not a number");
    internal static bool Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new InvalidDataException($"'{key}' missing or not a boolean");

    /// <summary>
    /// The file writer.exe / the GUI generate when no settings exist (DriverConfig.GetDefault().ToJSON()),
    /// reconstructed from wrapper.cpp and rawaccel-base.hpp defaults.
    /// </summary>
    public static RawAccelSettingsDocument CreateDefault(RawAccelKeyMap? keys = null)
    {
        var k = keys ?? RawAccelKeyMap.V1_7_0;
        JsonObject Vec(double x, double y) => new() { [k.X] = x, [k.Y] = y };
        JsonObject Accel() => new()
        {
            [k.Mode] = "noaccel", [k.Gain] = true, [k.InputOffset] = 0.0, [k.OutputOffset] = 0.0, [k.Acceleration] = 0.005,
            [k.DecayRate] = 0.1, [k.Gamma] = 1.0, [k.Motivity] = 1.5, [k.ExponentClassic] = 2.0, [k.Scale] = 1.0,
            [k.ExponentPower] = 0.05, [k.Limit] = 1.5, [k.SyncSpeed] = 5.0, [k.Smooth] = 0.5, [k.Cap] = Vec(15, 1.5),
            [k.CapMode] = "output", [k.Data] = new JsonArray(),
        };
        var root = new JsonObject
        {
            ["### Accel modes ###"] = string.Join(" | ", k.AccelModes),
            ["### Cap modes ###"] = string.Join(" | ", k.CapModes),
            [k.Version] = k.SettingsVersion,
            [k.DefaultDeviceConfig] = new JsonObject
            {
                [k.Disable] = false, [k.PollTimeLock] = false, [k.Dpi] = 0, [k.PollingRate] = 0,
            },
            [k.Profiles] = new JsonArray(new JsonObject
            {
                [k.Name] = "default",
                [k.DomainWeights] = Vec(1, 1),
                [k.RangeWeights] = Vec(1, 1),
                [k.AccelX] = Accel(),
                [k.AccelY] = Accel(),
                [k.SpeedArgs] = new JsonObject
                {
                    [k.Whole] = true, [k.LpNorm] = 2.0, [k.InputSmoothHalflife] = 0.0, [k.ScaleSmoothHalflife] = 0.0, [k.OutputSmoothHalflife] = 0.0,
                },
                [k.OutputDpi] = 1000.0, [k.YxRatio] = 1.0, [k.LrRatio] = 1.0, [k.UdRatio] = 1.0,
                [k.Rotation] = 0.0, [k.Snap] = 0.0, [k.SpeedCap] = 0.0,
            }),
            [k.Devices] = new JsonArray(),
        };
        return new RawAccelSettingsDocument(root, k);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Raw Accel settings v{Keys.SettingsVersion}, {ProfileNames.Count} profile(s)");
}
