using OpenVector.Core.Analysis;
using OpenVector.Core.Profiles;

namespace OpenVector.Core.Tests;

/// <summary>Synthetic users with known injected biases are recovered; regularisation, bounds and shrinkage behave.</summary>
public class RecoveryTests
{
    private static readonly GainFitOptions Opts = new() { BootstrapReps = 100, Seed = 7 };

    public static TheoryData<string> Users => new() { "UpUndershoot", "Diagonal", "LeftRight", "Rich" };

    private static double[] BiasOf(string name) => name switch
    {
        "UpUndershoot" => Synthetic.UpUndershoot, "Diagonal" => Synthetic.Diagonal,
        "LeftRight" => Synthetic.LeftRight, "Rich" => Synthetic.Rich, _ => Synthetic.Identity,
    };

    [Theory]
    [MemberData(nameof(Users))]
    public void InjectedBiasIsRecoveredWithinCi(string user)
    {
        var bias = BiasOf(user);
        double cover = 0, maxErr = 0;
        int[] seeds = [1, 2, 3];
        foreach (int seed in seeds)
        {
            var fit = GainMapEstimator.Fit(Synthetic.Session(bias, seed), Opts);
            int inside = 0;
            double err = 0;
            foreach (var b in fit.Bins)
            {
                double truth = Synthetic.TrueGain(bias, b.Theta);
                if (truth >= b.CiLow && truth <= b.CiHigh) inside++;
                err = Math.Max(err, Math.Abs(b.Gain - truth));
            }
            cover += (double)inside / fit.Bins.Length / seeds.Length;
            maxErr += err / seeds.Length;
        }
        Assert.True(cover >= 0.75, $"CI coverage {cover:F2}");
        Assert.True(maxErr < 0.035, $"mean max error {maxErr:F4}");
    }

    [Fact]
    public void UpUndershootGivesMoreGainUp()
    {
        var fit = GainMapEstimator.Fit(Synthetic.Session(Synthetic.UpUndershoot, 4), Opts);
        Assert.True(fit.Bins[48].Gain > 1.06, $"up gain {fit.Bins[48].Gain}");   // 3π/2: 1/0.92 ≈ 1.087
        Assert.True(Math.Abs(fit.Bins[16].Gain - 1.0) < 0.02);                    // π/2 (down)
        Assert.True(fit.Bins[48].CiLow > 1.0);
    }

    [Fact]
    public void DiagonalSin2ThetaIsResolved()
    {
        var fit = GainMapEstimator.Fit(Synthetic.Session(Synthetic.Diagonal, 5), Opts);
        Assert.True(fit.Order >= 2);
        Assert.True(fit.Bins[8].Gain < 0.97 && fit.Bins[24].Gain > 1.03, "θ=π/4 overshoots, θ=3π/4 undershoots");
    }

    [Fact]
    public void IdentityUserStaysNearOne()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var fit = GainMapEstimator.Fit(Synthetic.Session(Synthetic.Identity, seed), Opts);
            Assert.All(fit.Bins, b => Assert.InRange(b.Gain, 0.985, 1.015));
            Assert.True(fit.Order <= 2, $"order {fit.Order}");
        }
    }

    [Fact]
    public void BoundsAreRespected()
    {
        double[] huge = [0, 0, 0, 0, 0, 0, 0, 0, 0.3];
        var fit = GainMapEstimator.Fit(Synthetic.Session(huge, 6, reps: 4, sigmaAlong: 0.08), Opts);
        Assert.All(fit.Bins, b =>
        {
            Assert.InRange(b.Gain, 0.85, 1.15);
            Assert.InRange(b.CiLow, 0.85, 1.15);
            Assert.InRange(b.CiHigh, 0.85, 1.15);
        });
        Assert.Equal(1.15, fit.Bins.Max(b => b.Gain));
        var custom = GainMapEstimator.Fit(Synthetic.Session(huge, 6, reps: 4), Opts with { Bounds = new GainBounds { MinGain = 0.75, MaxGain = 1.25 } });
        Assert.True(custom.Bins.Max(b => b.Gain) > 1.2);
        Assert.Equal(1.15, fit.Model.Gain(Math.PI * 3 / 8, GainBounds.Default));
    }

    [Fact]
    public void EmptyHalfShrinksToIdentity()
    {
        double[] fast = [-0.06]; // +6.4 % everywhere
        var trials = Synthetic.Session(fast, 6, filter: p => p.Sector <= 8 || p.Sector >= 24);
        var fit = GainMapEstimator.Fit(trials, Opts);
        Assert.True(Math.Abs(fit.Bins[0].Gain - 1 / 0.94) < 0.02, "data-rich direction recovered");
        Assert.True(Math.Abs(fit.Bins[32].Gain - 1.0) < 0.02, $"no-data direction → 1.0, got {fit.Bins[32].Gain}");
        Assert.Subset(fit.SparseStrata.ToHashSet(), Enumerable.Range(11, 11).ToHashSet());
    }

    [Fact]
    public void EmptyStratumWithDenseNeighboursTakesTheirValue()
    {
        double[] fast = [-0.06];
        var fit = GainMapEstimator.Fit(Synthetic.Session(fast, 7, filter: p => p.Sector != 16), Opts);
        Assert.Contains(16, fit.SparseStrata);
        Assert.True(Math.Abs(fit.Bins[32].Gain - 1 / 0.94) < 0.02);
    }

    [Fact]
    public void ProjectionSamplesFollowTrialDensity()
    {
        var trials = Synthetic.Session(Synthetic.LeftRight, 8, filter: p => p.Sector < 16 || p.AmplitudeIndex == 0);
        var profile = GainMapEstimator.Fit(trials, Opts with { BootstrapReps = 0 })
            .ToProfile("p", DateTimeOffset.UnixEpoch, new ProfileMapping { YawDegPerCount = 1, PitchDegPerCount = 1 }, []);
        var s = profile.SampleForProjection(360);
        Assert.Equal(360, s.Theta.Length);
        Assert.Equal(1.0, s.Weight.Average(), 12);
        Assert.True(s.Weight[90] > 2 * s.Weight[270], "down half has ~3x the trials of the up half");
        Assert.True(s.Gain[0] < 1.0 && s.Gain[180] > 1.0, "right overshoots → less gain");
        Assert.All(s.Weight, w => Assert.True(w > 0));
    }

    [Fact]
    public void TrialPlanIsBalancedRandomisedAndDeterministic()
    {
        var o = new TrialPlanOptions { Seed = 99 };
        var a = TrialPlanGenerator.Generate(o);
        Assert.Equal(32 * 3 * 7, a.Count);
        Assert.All(a.GroupBy(p => (p.Sector, p.AmplitudeIndex)), g => Assert.Equal(7, g.Count()));
        Assert.All(a, p => Assert.Equal(p.Sector, Angles.SectorOf(p.Theta, 32)));
        Assert.Equal(a, TrialPlanGenerator.Generate(o));
        Assert.NotEqual(a.Select(p => p.Theta), TrialPlanGenerator.Generate(o with { Seed = 100 }).Select(p => p.Theta));
        Assert.True(a.Select(p => p.Theta).Distinct().Count() == a.Count, "continuous angles");
    }

    [Fact]
    public void SequentialEffectsAreReportedNotApplied()
    {
        var trials = Synthetic.Session(Synthetic.Diagonal, 3, reversalBias: -0.04, reactionUpExtraUs: 30_000);
        var fit = GainMapEstimator.Fit(trials, Opts with { BootstrapReps = 0 });
        var seq = SequentialEffects.Compute(trials, fit.Model);
        var d = seq.ReversalMinusSame!;
        Assert.True(Math.Abs(d.Diff - -0.04) < 3 * d.Se, $"reversal − same = {d.Diff:F4} ± {d.Se:F4}");
        Assert.True(seq.ReactionTime[6].MeanUs - seq.ReactionTime[2].MeanUs > 20_000, "up slower than down");

        var plain = Synthetic.Session(Synthetic.Rich, 10);
        var p = SequentialEffects.Compute(plain, GainMapEstimator.Fit(plain, Opts with { BootstrapReps = 0 }).Model).ReversalMinusSame!;
        Assert.True(Math.Abs(p.Diff) < 3 * p.Se);
    }

    [Fact]
    public void ExcludedUnknownAndOutlierTrialsAreNotUsed()
    {
        var trials = Synthetic.Session(Synthetic.Identity, 2, reps: 2);
        trials[0] = trials[0] with { ExcludedReason = Data.ExcludedReason.Miss };
        trials[1] = trials[1] with { Provenance = trials[1].Provenance with { TargetTruth = Data.TargetTruth.Unknown } };
        trials[2] = trials[2] with { Outcome = trials[2].Outcome with { EndpointPrimary = Data.CountPoint.At(0, 0) } }; // along = −1
        var s = GainMapEstimator.ExtractSignal(trials);
        Assert.Equal(trials.Count - 3, s.Theta.Length);
    }
}
