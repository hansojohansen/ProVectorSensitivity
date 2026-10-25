using OpenVector.RawAccel.Model;
using OpenVector.RawAccel.Projection;
using OpenVector.RawAccel.Settings;

namespace OpenVector.RawAccel.Tests;

public class ProjectionTests
{
    private static readonly RawAccel17Projection Backend = new();

    private static ProjectionTarget Target(System.Text.Json.JsonElement fit)
    {
        var g = fit.Arr("target");
        return new ProjectionTarget(Golden.Theta(g.Length), g);
    }

    public static IEnumerable<object[]> GoldenFits()
    {
        foreach (var f in Golden.Root.GetProperty("fits").EnumerateArray())
            foreach (var ft in f.GetProperty("fits").EnumerateArray())
                yield return new object[] { f.GetProperty("name").GetString()!, ft.D("lam") };
    }

    [Theory]
    [MemberData(nameof(GoldenFits))]
    public void MatchesScipyLeastSquares(string name, double lam)
    {
        var f = Golden.Root.GetProperty("fits").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var ft = f.GetProperty("fits").EnumerateArray().Single(x => x.D("lam") == lam);
        var r = Backend.Project(Target(f), new ProjectionOptions { DirectionPenalty = lam });
        var expected = ft.Arr("params");
        var got = r.Params.ToArray();
        for (int i = 0; i < 5; i++) Assert.True(Math.Abs(expected[i] - got[i]) < 2e-3, $"{name} λ={lam} param {i}: scipy {expected[i]:F5}, ours {got[i]:F5}");
        Assert.True(Math.Abs(ft.D("rms") - r.RmsResidual) < 2e-4, $"rms scipy {ft.D("rms")}, ours {r.RmsResidual}");
        Assert.True(Math.Abs(ft.D("max") - r.MaxAbsResidual) < 1e-3);
        Assert.True(Math.Abs(ft.D("max_dir_deg") - r.MaxTiltDeg) < 0.1);
        Assert.False(r.AxisOnly);
    }

    [Fact]
    public void PureSin2Theta_GivesNoImprovement()
    {
        var r = Backend.Project(ProjectionTarget.Sample(t => Math.Exp(0.08 * Math.Sin(2 * t)), 720));
        Assert.InRange(r.RmsResidual, 0.0560, 0.0572);     // A §2.3: 5.66 %
        Assert.InRange(r.BiasRms, 0.0560, 0.0572);
        Assert.InRange(r.FractionOfBiasApplied, 0, 0.01);
        Assert.InRange(r.ExplainedVariance, -0.01, 0.01);
        Assert.Equal(1, r.Params.Kappa, 3); Assert.Equal(1, r.Params.Rho, 3);
    }

    [Fact]
    public void FirstHarmonicPlusCos2Targets_FitBelowOnePercent()
    {
        var rng = new Random(7);
        var rms = new List<double>();
        for (int k = 0; k < 50; k++)
        {
            double a1 = Normal(rng) * 0.04, b1 = Normal(rng) * 0.04, a2 = Normal(rng) * 0.04;
            var r = Backend.Project(ProjectionTarget.Sample(t => Math.Exp(a1 * Math.Cos(t) + b1 * Math.Sin(t) + a2 * Math.Cos(2 * t)), 360),
                new ProjectionOptions { DirectionPenalty = 0 });
            rms.Add(r.RmsResidual);
        }
        rms.Sort();
        Assert.True(rms[rms.Count / 2] < 0.01, $"median {rms[rms.Count / 2]}"); // A §2.3: 0.83 %
    }

    [Fact]
    public void RandomSmoothTargets_ReproduceSection23Statistics()
    {
        var rng = new Random(1);
        var rms = new List<double>(); var ev = new List<double>();
        for (int k = 0; k < 200; k++)
        {
            var c = new double[9]; var s = new double[9];
            for (int h = 1; h <= 4; h++) { c[h] = 0.05 / h * Normal(rng); s[h] = 0.05 / h * Normal(rng); }
            var r = Backend.Project(ProjectionTarget.Sample(t =>
            {
                double lg = 0;
                for (int h = 1; h <= 4; h++) lg += c[h] * Math.Cos(h * t) + s[h] * Math.Sin(h * t);
                return Math.Exp(lg);
            }, 360), new ProjectionOptions { DirectionPenalty = 0, MinRatio = 0.25, MaxRatio = 4, MinKappa = 0.25, MaxKappa = 4 });
            rms.Add(r.RmsResidual); ev.Add(r.ExplainedVariance);
        }
        rms.Sort(); ev.Sort();
        Assert.InRange(rms[100], 0.020, 0.031);   // A §2.3: median 2.5 %
        Assert.InRange(ev[100], 0.65, 0.87);      // A §2.3: ~76 %
    }

    [Fact]
    public void ReachableTarget_IsRecoveredExactly()
    {
        var truth = new DirectionalParams(1.03, 0.93, 1.06, 1.08, 0.95);
        var t = ProjectionTarget.Sample(th => RawAccel17Projection.Evaluate(th, truth.G0, truth.Kappa, truth.Rho, truth.L, truth.U, ProjectionBaseline.Identity).Gain, 360);
        var r = Backend.Project(t, new ProjectionOptions { DirectionPenalty = 0 });
        Assert.True(r.RmsResidual < 1e-7, r.RmsResidual.ToString());
        Assert.Equal(truth.Kappa, r.Params.Kappa, 5);
        Assert.Equal(truth.U, r.Params.U, 5);
        Assert.True(r.ExplainedVariance > 0.999999);
        Assert.True(r.FractionOfBiasApplied > 0.9999);
    }

    [Fact]
    public void DirectionPenalty_ReducesTilt()
    {
        var t = ProjectionTarget.Sample(th => Math.Exp(0.05 * Math.Cos(th) - 0.04 * Math.Sin(th) + 0.03 * Math.Cos(2 * th)), 360);
        var free = Backend.Project(t, new ProjectionOptions { DirectionPenalty = 0 });
        var pen = Backend.Project(t, new ProjectionOptions { DirectionPenalty = 3 });
        Assert.True(pen.MaxTiltDeg < free.MaxTiltDeg);
        Assert.True(pen.RmsResidual >= free.RmsResidual - 1e-9);
    }

    [Fact]
    public void AxisOnly_WhenBaselineHasAccelCurve()
    {
        var t = ProjectionTarget.Sample(th => Math.Exp(0.05 * Math.Cos(th) + 0.03 * Math.Cos(2 * th)), 360);
        var bl = new ProjectionBaseline { ShapeAvailable = false, ShapeUnavailableReason = "classic curve" };
        var r = Backend.Project(t, null, bl);
        Assert.True(r.AxisOnly);
        Assert.Equal(1.0, r.Params.Kappa);
        Assert.Contains(r.Warnings, w => w.Contains("acceleration curve"));
        Assert.Throws<InvalidOperationException>(() => Backend.Project(t, new ProjectionOptions { Mode = ProjectionMode.Full }, bl));
        var forced = Backend.Project(t, new ProjectionOptions { Mode = ProjectionMode.AxisOnly });
        Assert.True(forced.AxisOnly); Assert.Equal(1.0, forced.Params.Kappa);
    }

    [Fact]
    public void Bounds_AreRespected()
    {
        var t = ProjectionTarget.Sample(th => Math.Cos(th) < 0 ? 3.0 : 1.0, 360);
        var r = Backend.Project(t, new ProjectionOptions { DirectionPenalty = 0, MaxRatio = 1.2 });
        Assert.True(r.Params.L <= 1.2 + 1e-12);
        Assert.Contains(r.Warnings, w => w.Contains("bound"));
    }

    [Fact]
    public void Weights_FocusTheFit()
    {
        // 8-direction target; weight only the four axes -> axis gains are matched (4 axes, 5 DOF)
        var th = Golden.Theta(360);
        var g = th.Select(t => Math.Exp(0.04 * Math.Cos(t) + 0.03 * Math.Sin(t) + 0.05 * Math.Sin(2 * t) + 0.02 * Math.Cos(3 * t))).ToArray();
        var w = th.Select((t, i) => i % 90 == 0 ? 1.0 : 0.0).ToArray();
        var r = Backend.Project(new ProjectionTarget(th, g, w), new ProjectionOptions { DirectionPenalty = 0 });
        for (int i = 0; i < 360; i += 90) Assert.True(Math.Abs(r.Residual.RelativeResidual[i]) < 1e-6);
        Assert.True(r.RmsResidual < 1e-6);
    }

    [Fact]
    public void InvalidTargets_Throw()
    {
        Assert.Throws<ArgumentException>(() => Backend.Project(new ProjectionTarget(new[] { 0.0 }, new[] { -1.0 })));
        Assert.Throws<ArgumentException>(() => Backend.Project(new ProjectionTarget(new[] { 0.0, 1 }, new[] { 1.0 })));
        Assert.Throws<ArgumentException>(() => Backend.Project(new ProjectionTarget(new[] { 0.0 }, new[] { 1.0 }, new[] { 0.0 })));
    }

    [Theory]
    [InlineData(0.0, 1.0, 1.0, 1.0)]
    [InlineData(5.0, 1.1, 0.9, 1.05)]
    [InlineData(-3.0, 0.95, 1.08, 0.9)]
    public void AppliedSettings_ReproducePredictedGain_InExactDriverModel(double rotation, double rhoB, double lB, double uB)
    {
        // end to end: fit -> compose with baseline -> real Raw Accel fields -> exact modify() port
        var baselineOwned = new OwnedProfileValues { OutputDpi = 800, YxRatio = rhoB, LrRatio = lB, UdRatio = uB, Whole = true };
        var pb = new ProjectionBaseline { YxRatio = rhoB, LrRatio = lB, UdRatio = uB, RotationDeg = rotation };
        var target = ProjectionTarget.Sample(t => Math.Exp(0.04 * Math.Cos(t) - 0.03 * Math.Sin(t) + 0.05 * Math.Cos(2 * t) + 0.02 * Math.Sin(2 * t)), 360);
        var r = Backend.Project(target, null, pb);
        Assert.NotEqual(1.0, r.Params.Kappa);
        var owned = RawAccelParameterMapping.Compose(baselineOwned, r.Params);

        RawAccelProfile ToProfile(OwnedProfileValues o) => new()
        {
            OutputDpi = o.OutputDpi, YxOutputDpiRatio = o.YxRatio, LrOutputDpiRatio = o.LrRatio, UdOutputDpiRatio = o.UdRatio,
            RangeWeightX = o.RangeWeightX, RangeWeightY = o.RangeWeightY, Whole = o.Whole, AccelX = o.AccelX, DegreesRotation = rotation,
        };
        var before = new RawAccelModel(ToProfile(baselineOwned));
        var after = new RawAccelModel(ToProfile(owned));
        for (int i = 0; i < 360; i += 7)
        {
            double th = target.Theta[i];
            var (gb, pb0) = before.Evaluate(th, 3.7, 0.625);
            var (ga, pa) = after.Evaluate(th, 3.7, 0.625);
            Assert.True(Math.Abs(ga / gb - r.Residual.AppliedGain[i]) < 1e-9, $"θ={th}: model {ga / gb}, projection {r.Residual.AppliedGain[i]}");
            Assert.True(Math.Abs(RawAccelModel.WrapAngle(pa - pb0) * 180 / Math.PI - r.Residual.TiltDeg[i]) < 1e-7);
        }
    }

    [Fact]
    public void Compose_MapsKappaToRangeWeightsAndConstantLut()
    {
        var b = new OwnedProfileValues { OutputDpi = 1200 };
        var o = RawAccelParameterMapping.Compose(b, new DirectionalParams(1.1, 0.9, 1.05, 1.02, 0.97));
        Assert.Equal(AccelMode.Lut, o.AccelX.Mode);
        Assert.False(o.AccelX.Gain);
        Assert.Equal(new float[] { 1, 2, 2, 2 }, o.AccelX.Data);
        Assert.Equal(1, o.RangeWeightX); Assert.Equal(0.8, o.RangeWeightY, 12);
        Assert.Equal(1200 * 1.1 / 2, o.OutputDpi, 9);
        Assert.Equal(1.05, o.YxRatio, 12);
        var axis = RawAccelParameterMapping.Compose(b, new DirectionalParams(1.1, 1, 1.05, 1.02, 0.97));
        Assert.Equal(AccelMode.NoAccel, axis.AccelX.Mode);
        Assert.Equal(1320, axis.OutputDpi, 9);
        var curve = b with { AccelX = new AccelCurve { Mode = AccelMode.Classic } };
        Assert.Throws<InvalidOperationException>(() => RawAccelParameterMapping.Compose(curve, new DirectionalParams(1, 0.9, 1, 1, 1)));
        // κ < 0.5 needs c > 2 so that ry >= 0
        var low = RawAccelParameterMapping.Compose(b, new DirectionalParams(1, 0.3, 1, 1, 1));
        Assert.True(low.RangeWeightY >= 0);
        double c = low.AccelX.Data[1];
        Assert.Equal(0.3, (1 + (c - 1) * low.RangeWeightY) / (1 + (c - 1) * low.RangeWeightX), 9);
    }

    [Fact]
    public void InterpPeriodic_Wraps()
    {
        var th = new[] { 0.5, 2.0, 4.0 };
        var v = new[] { 1.0, 2.0, 3.0 };
        Assert.Equal(1.5, RawAccel17Projection.InterpPeriodic(th, v, 1.25), 12);
        double span = 2 * Math.PI - 4.0 + 0.5;            // last sample (4.0) wraps to first (0.5 + 2π)
        double frac = (0.25 + 2 * Math.PI - 4.0) / span;   // a = 0.25 lies before the first sample
        Assert.Equal(3 + (1 - 3) * frac, RawAccel17Projection.InterpPeriodic(th, v, 0.25), 12);
        Assert.Equal(1.0, RawAccel17Projection.InterpPeriodic(th, v, 0.5 + 2 * Math.PI), 12);
    }

    private static double Normal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
