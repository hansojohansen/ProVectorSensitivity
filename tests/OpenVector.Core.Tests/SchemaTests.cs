using System.Text.Json.Nodes;
using OpenVector.Core.Analysis;
using OpenVector.Core.Data;
using OpenVector.Core.Json;
using OpenVector.Core.Profiles;

namespace OpenVector.Core.Tests;

public class SchemaTests
{
    [Theory]
    [InlineData("movement-trial")]
    [InlineData("profile")]
    [InlineData("session")]
    public void ExampleValidatesAgainstSchema(string name) =>
        TestFiles.AssertValid(name, TestFiles.Read("schemas", "examples", name, "example.json"));

    private static void AssertRoundTrip<T>(string schema, string json)
    {
        var obj = OpenVectorJson.Deserialize<T>(json);
        string back = OpenVectorJson.Serialize(obj);
        TestFiles.AssertValid(schema, back);
        var a = TestFiles.StripNulls(JsonNode.Parse(json));
        var b = TestFiles.StripNulls(JsonNode.Parse(back));
        Assert.True(JsonNode.DeepEquals(a, b), $"round trip changed {typeof(T).Name}:\n" + string.Join("\n", TestFiles.Diff(a, b).Take(10)));
    }

    [Fact]
    public void MovementTrialRoundTrips() =>
        AssertRoundTrip<MovementTrial>("movement-trial", TestFiles.Read("schemas", "examples", "movement-trial", "example.json"));

    [Fact]
    public void ProfileRoundTrips() =>
        AssertRoundTrip<Profile>("profile", TestFiles.Read("schemas", "examples", "profile", "example.json"));

    [Fact]
    public void SessionRoundTrips() =>
        AssertRoundTrip<Session>("session", TestFiles.Read("schemas", "examples", "session", "example.json"));

    [Fact]
    public void GoldenTrialsValidateAndRoundTrip()
    {
        var trials = TestFiles.Golden("fit_sparse.json")["trials"]!.AsArray();
        foreach (var t in trials.Take(40)) AssertRoundTrip<MovementTrial>("movement-trial", t!.ToJsonString());
    }

    [Fact]
    public void RequiredNullablesAreWrittenAsNull()
    {
        var t = OpenVectorJson.Deserialize<MovementTrial>(TestFiles.Read("schemas", "examples", "movement-trial", "example.json"));
        var noMove = t with
        {
            Outcome = t.Outcome with { EndpointPrimary = null },
            Kinematics = new TrialKinematics(),
            ExcludedReason = ExcludedReason.SegmentationAmbiguous,
        };
        string json = OpenVectorJson.Serialize(noMove);
        Assert.Contains("\"endpoint_primary\":null", json);
        Assert.Contains("\"peak_speed_counts_per_s\":null", json);
        TestFiles.AssertValid("movement-trial", json);
    }

    [Fact]
    public void FittedProfileValidates()
    {
        var trials = Synthetic.Session(Synthetic.Rich, seed: 3);
        var fit = GainMapEstimator.Fit(trials, new GainFitOptions { BootstrapReps = 20 });
        var profile = fit.ToProfile("p-1", DateTimeOffset.UnixEpoch, new ProfileMapping { YawDegPerCount = 0.022, PitchDegPerCount = 0.022, Dpi = 800 }, ["s1"]);
        var withProjection = profile with
        {
            Projections = new() { ["rawaccel-1.7"] = new ProjectionEntry { Params = new JsonObject { ["y_x_ratio"] = 1.02 }, ResidualRms = 0.01 } },
            Validation = new ValidationResult { Status = ValidationStatus.Pending },
        };
        string json = OpenVectorJson.Serialize(withProjection, indented: true);
        TestFiles.AssertValid("profile", json);
        var back = OpenVectorJson.Deserialize<Profile>(json);
        Assert.Equal(64, back.Bins.Length);
        Assert.Equal(profile.Gain(1.0), back.Gain(1.0));
        Assert.Equal(1.02, back.Projections["rawaccel-1.7"].Params["y_x_ratio"]!.GetValue<double>());
    }

    [Fact]
    public void SpeedDimensionIsRefusedInV1()
    {
        var p = OpenVectorJson.Deserialize<Profile>(TestFiles.Read("schemas", "examples", "profile", "example.json"));
        var withSpeed = p with { Model = p.Model with { Speed = new SpeedDimension { Degree = 3, KnotsLnSpeed = [0, 1], Coeffs2D = [[0.0]] } } };
        TestFiles.AssertValid("profile", OpenVectorJson.Serialize(withSpeed));
        Assert.Throws<NotSupportedException>(() => withSpeed.Gain(0));
    }
}
