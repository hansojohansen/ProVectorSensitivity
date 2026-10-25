using OpenVector.Core.Analysis;
using OpenVector.Core.Data;
using OpenVector.Core.Json;

namespace OpenVector.Core.Tests;

public class SegmenterTests
{
    private static SegmentationResult Seg((double, double, double, double)[] subs, double end = 0.7, double hz = 1000, double dir = 0)
    {
        var (t, dx, dy) = Synthetic.Packets(subs, end, hz);
        return Segmenter.Segment(t, dx, dy, 0, dir)!;
    }

    [Fact]
    public void SingleFlick()
    {
        var r = Seg([(0.2, 0.25, 800, -300)], dir: Math.Atan2(-300, 800));
        Assert.Equal(0, r.NCorrections);
        Assert.True(Math.Abs(r.PrimaryEndX - 800) < 8 && Math.Abs(r.PrimaryEndY + 300) < 8);
        Assert.InRange(r.TPeakUs, 320_000, 330_000);
    }

    [Fact]
    public void CorrectionAfterPause() =>
        Assert.Equal(1, Seg([(0.2, 0.25, 900, 0), (0.5, 0.1, 80, 10)]).NCorrections);

    [Fact]
    public void OverlappingCorrectionSplitAtDip()
    {
        var r = Seg([(0.2, 0.25, 900, 0), (0.43, 0.12, 120, 0)]);
        Assert.Equal(1, r.NCorrections);
        Assert.InRange(r.PrimaryEndX, 850, 920);
    }

    [Fact]
    public void OvershootReversal()
    {
        var r = Seg([(0.2, 0.25, 1000, 0), (0.47, 0.12, -100, 0)]);
        Assert.Equal(1, r.NCorrections);
        Assert.True(r.Submovements[1].Dx < 0);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(8000)]
    public void PollingRates(double hz)
    {
        var r = Seg([(0.2, 0.25, 800, 0)], hz: hz);
        Assert.Equal(0, r.NCorrections);
        Assert.True(Math.Abs(r.PrimaryEndX - 800) < 12);
    }

    [Fact]
    public void NoMovement()
    {
        Assert.Null(Segmenter.Segment([], [], []));
        Assert.Null(Segmenter.Segment([1000L], [0], [0]));
    }

    [Fact]
    public void AnnotateFillsDerivedFields()
    {
        var t = OpenVectorJson.Deserialize<MovementTrial>(TestFiles.Read("schemas", "examples", "movement-trial", "example.json"));
        var bare = t with
        {
            Outcome = t.Outcome with { EndpointPrimary = null, Errors = null },
            Timing = new TrialTiming { TStimulusUs = t.Timing.TStimulusUs, TStimulusOnScreenUs = t.Timing.TStimulusOnScreenUs, TFinalClickUs = t.Timing.TFinalClickUs },
            Kinematics = new TrialKinematics(),
        };
        var a = Segmenter.Annotate(bare);
        Assert.Equal(t.Outcome.EndpointPrimary!.XCounts, a.Outcome.EndpointPrimary!.XCounts, 9);
        Assert.Equal(t.Timing.TOnsetUs, a.Timing.TOnsetUs);
        Assert.Equal(t.Timing.ReactionTimeUs, a.Timing.ReactionTimeUs);
        Assert.Equal(ReactionTimeBasis.OnScreen, a.Timing.ReactionTimeBasis);
        Assert.Equal(t.Kinematics.NCorrections, a.Kinematics.NCorrections);
        Assert.Equal(t.Outcome.Errors!.AlongPrimary!.Value, a.Outcome.Errors!.AlongPrimary!.Value, 9);
        TestFiles.AssertValid("movement-trial", OpenVectorJson.Serialize(a));
        Assert.NotNull(TrialMetrics.Compute(bare)!.AlongPrimary); // metrics segment on demand
    }
}
