using System.Text.Json.Nodes;
using OpenVector.RawAccel.Model;
using OpenVector.RawAccel.Settings;

namespace OpenVector.RawAccel.Tests;

public class SettingsTests
{
    internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-1.7.0.json");
    internal static string FixtureText => File.ReadAllText(FixturePath);
    private static RawAccelKeyMap K => RawAccelKeyMap.V1_7_0;

    [Fact]
    public void Fixture_ParsesAndValidatesClean()
    {
        var doc = RawAccelSettingsDocument.Parse(FixtureText);
        Assert.Equal(new[] { "default", "Classic for desktop" }, doc.ProfileNames);
        var rep = RawAccelSettingsValidator.Validate(doc);
        Assert.True(rep.IsValid, rep.ToString());
        Assert.Empty(rep.Issues);
        Assert.Equal(1600, doc.DefaultDeviceDpi);
    }

    [Fact]
    public void Default_ValidatesClean_AndMatchesFixtureShape()
    {
        var def = RawAccelSettingsDocument.CreateDefault();
        Assert.Empty(RawAccelSettingsValidator.Validate(def).Issues);
        var fx = RawAccelSettingsDocument.Parse(FixtureText);
        var a = (JsonObject)def.Root[K.Profiles]![0]!; var b = (JsonObject)fx.Root[K.Profiles]![0]!;
        Assert.Equal(b.Select(kv => kv.Key), a.Select(kv => kv.Key));
        Assert.Equal(((JsonObject)b[K.AccelX]!).Select(kv => kv.Key), ((JsonObject)a[K.AccelX]!).Select(kv => kv.Key));
        Assert.Equal(fx.Root.Select(kv => kv.Key), def.Root.Select(kv => kv.Key));
    }

    [Fact]
    public void NoOpRoundTrip_IsByteIdentical()
    {
        var doc = RawAccelSettingsDocument.Parse(FixtureText);
        doc.WriteOwned("default", doc.ReadOwned("default"));
        Assert.Equal(FixtureText.ReplaceLineEndings().TrimEnd(), doc.ToJson().ReplaceLineEndings().TrimEnd());
    }

    [Theory]
    [InlineData("1.6.1")]
    [InlineData("1.7.1")]
    [InlineData("2.0.0")]
    [InlineData(null)]
    public void UnsupportedVersions_AreRefused(string? version)
    {
        var node = JsonNode.Parse(FixtureText)!.AsObject();
        if (version is null) node.Remove("version"); else node["version"] = version;
        var e = Assert.Throws<UnsupportedRawAccelVersionException>(() => RawAccelSettingsDocument.Parse(node.ToJsonString()));
        Assert.Equal(version, e.Version);
    }

    [Fact]
    public void WriteOwned_TouchesOnlyOwnedFieldsOfSelectedProfile()
    {
        var node = JsonNode.Parse(FixtureText)!.AsObject();
        node["x-unknown-top"] = new JsonObject { ["keep"] = 1 };
        ((JsonObject)node[K.Profiles]![0]!)["x-unknown-in-profile"] = "keep me";
        var doc = RawAccelSettingsDocument.Parse(node.ToJsonString());
        var before = (JsonObject)doc.Root.DeepClone();

        var owned = new OwnedProfileValues
        {
            OutputDpi = 812.5, YxRatio = 1.04, LrRatio = 1.07, UdRatio = 0.96, RangeWeightX = 1, RangeWeightY = 0.86,
            Whole = true, AccelX = AccelCurve.ConstantSensitivity(2),
        };
        doc.WriteOwned("default", owned);
        Assert.True(doc.ReadOwned("default").ApproximatelyEquals(owned));
        var after = JsonNode.Parse(doc.ToJson())!.AsObject();

        // everything except the owned paths of profile 0 is unchanged
        var ownedPaths = new HashSet<string>
        {
            $"$.profiles[0]['{K.OutputDpi}']", $"$.profiles[0]['{K.YxRatio}']", $"$.profiles[0]['{K.LrRatio}']", $"$.profiles[0]['{K.UdRatio}']",
            $"$.profiles[0]['{K.RangeWeights}'].y", $"$.profiles[0]['{K.AccelX}'].mode", $"$.profiles[0]['{K.AccelX}']['{K.Gain}']",
            $"$.profiles[0]['{K.AccelX}'].data",
        };
        var diffs = new List<string>();
        Diff(before, after, diffs);
        Assert.All(diffs, d => Assert.Contains(d, ownedPaths));
        Assert.Contains($"$.profiles[0]['{K.OutputDpi}']", diffs);
        Assert.Equal("keep me", after[K.Profiles]![0]!["x-unknown-in-profile"]!.GetValue<string>());
        Assert.Equal(1, after["x-unknown-top"]!["keep"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(before[K.Profiles]![1], after[K.Profiles]![1]));
        Assert.True(JsonNode.DeepEquals(before[K.Devices], after[K.Devices]));
        // key order preserved
        Assert.Equal(((JsonObject)before[K.Profiles]![0]!).Select(kv => kv.Key), ((JsonObject)after[K.Profiles]![0]!).Select(kv => kv.Key));
        // unknown key in a profile is a warning only
        var rep = RawAccelSettingsValidator.Validate(doc);
        Assert.True(rep.IsValid);
        Assert.Contains(rep.Issues, i => i.Severity == IssueSeverity.Warning && i.Path.Contains("x-unknown-in-profile"));
        // Unicode device names are written unescaped
        Assert.Contains("Ünicode", doc.ToJson());
    }

    private static void Diff(JsonNode? a, JsonNode? b, List<string> diffs)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var kv in oa)
                if (!JsonNode.DeepEquals(kv.Value, ob[kv.Key]))
                {
                    if ((kv.Value is JsonObject && ob[kv.Key] is JsonObject) || (kv.Value is JsonArray va && ob[kv.Key] is JsonArray vb && va.Count == vb.Count)) Diff(kv.Value, ob[kv.Key], diffs);
                    else diffs.Add(kv.Value!.GetPath());
                }
        }
        else if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
        { for (int i = 0; i < aa.Count; i++) Diff(aa[i], ab[i], diffs); }
        else if (!JsonNode.DeepEquals(a, b)) diffs.Add(a!.GetPath());
    }

    [Fact]
    public void ProfileSelection()
    {
        var doc = RawAccelSettingsDocument.Parse(FixtureText);
        Assert.Equal("default", doc.ResolveProfileName(null));
        Assert.Equal(1600, doc.ReadOwned("Classic for desktop").OutputDpi);
        Assert.Equal(AccelMode.Classic, doc.ReadOwned("Classic for desktop").AccelX.Mode);
        Assert.Throws<KeyNotFoundException>(() => doc.ReadOwned("nope"));
        var p = doc.ReadProfile("default");
        Assert.Equal(2.5, p.DegreesRotation); Assert.Equal(0.95, p.YxOutputDpiRatio);
    }

    public static IEnumerable<object[]> InvalidEdits() => new[]
    {
        new object[] { "Degrees of angle snapping", 50.0, "snap angle" },
        new object[] { "Degrees of angle snapping", -1.0, "snap angle" },
        new object[] { "Output DPI", 0.0, "output DPI is 0" },
        new object[] { "Y/X output DPI ratio (vertical sens multiplier)", 0.0, "Y/X output DPI ratio is 0" },
        new object[] { "L/R output DPI ratio (left sens multiplier)", -1.0, "must be positive" },
        new object[] { "U/D output DPI ratio (up sens multiplier)", 0.0, "must be positive" },
        new object[] { "Input Speed Cap", -1.0, "speed cap is negative" },
    };

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public void Validator_ProfileLimits(string key, double value, string message)
    {
        var doc = RawAccelSettingsDocument.Parse(FixtureText);
        doc.GetProfileNode(null)[key] = value;
        var rep = RawAccelSettingsValidator.Validate(doc);
        Assert.False(rep.IsValid);
        Assert.Contains(rep.Errors, e => e.Message.Contains(message));
    }

    [Fact]
    public void Validator_NestedLimits()
    {
        RawAccelSettingsDocument Edit(Action<JsonObject> f) { var d = RawAccelSettingsDocument.Parse(FixtureText); f(d.GetProfileNode(null)); return d; }
        void Bad(Action<JsonObject> f, string msg)
        {
            var rep = RawAccelSettingsValidator.Validate(Edit(f));
            Assert.False(rep.IsValid, msg);
            Assert.Contains(rep.Errors, e => e.Message.Contains(msg));
        }
        Bad(p => p[K.RangeWeights]![K.Y] = -0.1, "range weights");
        Bad(p => p[K.DomainWeights]![K.X] = 0.0, "domain weights");
        Bad(p => p[K.SpeedArgs]![K.LpNorm] = 0.0, "Lp norm");
        Bad(p => { p[K.AccelX]![K.Mode] = "lut"; p[K.AccelX]![K.Data] = new JsonArray(1.0, 2.0); }, "at least 2 points");
        Bad(p => { p[K.AccelX]![K.Mode] = "lut"; p[K.AccelX]![K.Data] = new JsonArray(Enumerable.Range(0, 516).Select(i => (JsonNode?)JsonValue.Create(i * 1.0)).ToArray()); }, "max=257");
        Bad(p => p[K.AccelX]![K.Mode] = "linear", "invalid accel mode");
        Bad(p => p[K.AccelX]![K.CapMode] = "out", "invalid cap mode");
        Bad(p => p[K.AccelX]![K.Motivity] = 1.0, "motivity");
        Bad(p => p[K.AccelX]![K.Smooth] = 1.5, "smooth");
        Bad(p => p[K.AccelX]![K.Acceleration] = 0.0, "acceleration");
        Bad(p => ((JsonObject)p[K.AccelX]!).Remove(K.Acceleration), "key missing");
        Bad(p => p.Remove(K.Snap), "required key missing");
        Bad(p => p[K.Name] = "", "profile name can not be empty");
        Bad(p => { p[K.AccelX]![K.Mode] = "jump"; p[K.AccelX]![K.Cap]![K.X] = 0.0; }, "cap (input) can not be 0");
        Bad(p => p[K.OutputDpi] = "1000", "must be a number");

        // argsY is only validated in by-component mode
        var ok = Edit(p => p[K.AccelY]![K.Motivity] = 1.0);
        Assert.True(RawAccelSettingsValidator.Validate(ok).IsValid);
        Bad(p => { p[K.AccelY]![K.Motivity] = 1.0; p[K.SpeedArgs]![K.Whole] = false; }, "motivity");
    }

    [Fact]
    public void Validator_DeviceLimits()
    {
        var doc = RawAccelSettingsDocument.Parse(FixtureText);
        doc.Root[K.Devices]![0]![K.DeviceConfigKey]![K.Dpi] = -1;
        doc.Root[K.DefaultDeviceConfig]![K.MinimumTime] = 0.0;
        var rep = RawAccelSettingsValidator.Validate(doc);
        Assert.Contains(rep.Errors, e => e.Message.Contains("dpi can not be negative"));
        Assert.Contains(rep.Errors, e => e.Message.Contains("minimum time must be positive"));
        doc = RawAccelSettingsDocument.Parse(FixtureText);
        doc.Root[K.DefaultDeviceConfig]![K.MaximumTime] = 0.01;
        Assert.Contains(RawAccelSettingsValidator.Validate(doc).Errors, e => e.Message.Contains("max time is less than min time"));
        doc = RawAccelSettingsDocument.Parse(FixtureText);
        ((JsonObject)doc.Root[K.Devices]![0]!).Remove(K.DeviceId);
        Assert.Contains(RawAccelSettingsValidator.Validate(doc).Errors, e => e.Path.EndsWith(".id"));
    }

    [Fact]
    public void Neutral_Identity_RemovesOnlyOpenVectorLut()
    {
        var applied = new OwnedProfileValues { OutputDpi = 450, YxRatio = 1.1, LrRatio = 0.9, UdRatio = 1.05, RangeWeightX = 1, RangeWeightY = 0.8, AccelX = AccelCurve.ConstantSensitivity(2) };
        // no recorded baseline: keep the effective base gain (Output DPI × k(0))
        var n = NeutralProfile.Compute(applied, null);
        Assert.Equal(900, n.OutputDpi, 9);
        Assert.Equal((1.0, 1.0, 1.0), (n.YxRatio, n.LrRatio, n.UdRatio));
        Assert.Equal(AccelMode.NoAccel, n.AccelX.Mode);
        Assert.Equal((1.0, 1.0), (n.RangeWeightX, n.RangeWeightY));
        // with a baseline: Output DPI = the user's prior value
        var baseline = new OwnedProfileValues { OutputDpi = 800, YxRatio = 0.95, Whole = false };
        var nb = NeutralProfile.Compute(applied, baseline);
        Assert.Equal(800, nb.OutputDpi); Assert.Equal(1, nb.YxRatio); Assert.False(nb.Whole);
        Assert.Equal(baseline, NeutralProfile.Compute(applied, baseline, NeutralMode.RestoreBaseline));
        // a user's own curve and range weights are never touched
        var user = new OwnedProfileValues { OutputDpi = 1000, YxRatio = 1.2, RangeWeightY = 0.5, AccelX = new AccelCurve { Mode = AccelMode.Classic } };
        var nu = NeutralProfile.Compute(user, null);
        Assert.Equal(AccelMode.Classic, nu.AccelX.Mode); Assert.Equal(0.5, nu.RangeWeightY); Assert.Equal(1, nu.YxRatio);
        // a user's own non-constant LUT is not ours
        Assert.False(RawAccelParameterMapping.IsOpenVectorLut(new AccelCurve { Mode = AccelMode.Lut, Gain = false, Data = new float[] { 1, 2, 3, 4 } }));
    }

    [Fact]
    public void Neutral_WrittenToFixture_Validates()
    {
        var doc = RawAccelSettingsDocument.Parse(FixtureText);
        doc.WriteOwned(null, NeutralProfile.Compute(doc.ReadOwned(null), null));
        Assert.True(RawAccelSettingsValidator.Validate(doc).IsValid);
        Assert.Equal(1, doc.ReadOwned(null).YxRatio);
        Assert.Equal(800, doc.ReadOwned(null).OutputDpi);
    }
}

public class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ov-backup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void CreateListRestorePrune()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 10, 25, 13, 0, 0, TimeSpan.Zero));
        var store = new BackupStore(Path.Combine(_dir, "backups"), time) { MaxBackups = 3 };
        string src = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        var infos = new List<BackupInfo>();
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(src, $"content {i}");
            infos.Add(store.Create(src));
            if (i != 1) time.Advance(TimeSpan.FromSeconds(1)); // i = 1 and 2 collide -> suffix
        }
        var list = store.List();
        Assert.Equal(3, list.Count);
        Assert.Equal(infos[4].Path, list[0].Path);
        Assert.Contains("settings-20261025T130003000Z.json", list[0].Path);
        Assert.True(File.Exists(infos[4].Path));
        Assert.False(File.Exists(infos[0].Path));

        store.Restore(list[2], src);
        Assert.Equal("content 2", File.ReadAllText(src));
        Assert.Equal(RawAccelSettingsDocument.Sha256Hex(File.ReadAllBytes(src)), list[2].Sha256);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void DefaultDirectory_IsUnderLocalAppData()
    {
        Assert.EndsWith(Path.Combine("OpenVector", "backups"), BackupStore.DefaultDirectory);
    }
}

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan d) => _now += d;
}
