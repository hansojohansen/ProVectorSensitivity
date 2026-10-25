using OpenVector.RawAccel.Model;

namespace OpenVector.RawAccel.Tests;

public class ModelTests
{
    private static RawAccelProfile WholeFromGolden(System.Text.Json.JsonElement p) => new()
    {
        DegreesRotation = p.TryGetProperty("alpha_deg", out var a) ? a.GetDouble() : 0,
        AccelX = AccelCurve.ConstantSensitivity(p.D("c")),
        RangeWeightX = p.D("rx"), RangeWeightY = p.D("ry"),
        OutputDpi = 1000 * p.D("D"),
        YxOutputDpiRatio = p.D("rho"), LrOutputDpiRatio = p.D("L"), UdOutputDpiRatio = p.D("U"),
    };

    private static void Close(double expected, double actual, double tol = 1e-12) =>
        Assert.True(Math.Abs(expected - actual) <= tol * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

    [Fact]
    public void WholeMode_MatchesPythonReferenceModify()
    {
        int n = 0;
        foreach (var c in Golden.Root.GetProperty("modify").EnumerateArray())
        {
            var m = new RawAccelModel(WholeFromGolden(c.GetProperty("p")));
            var v = c.Arr("v"); var o = c.Arr("out");
            var (x, y) = m.Modify(v[0], v[1]);
            Close(o[0], x); Close(o[1], y); n++;
        }
        Assert.True(n > 300);
    }

    [Fact]
    public void ByComponentMode_MatchesReference()
    {
        foreach (var c in Golden.Root.GetProperty("by_component").EnumerateArray())
        {
            var p = c.GetProperty("p");
            var m = new RawAccelModel(new RawAccelProfile
            {
                Whole = false,
                DegreesRotation = p.D("alpha_deg"),
                AccelX = AccelCurve.ConstantSensitivity(p.D("cx")), AccelY = AccelCurve.ConstantSensitivity(p.D("cy")),
                RangeWeightX = p.D("rx"), RangeWeightY = p.D("ry"),
                OutputDpi = 1000 * p.D("D"), YxOutputDpiRatio = p.D("rho"), LrOutputDpiRatio = p.D("L"), UdOutputDpiRatio = p.D("U"),
            });
            var v = c.Arr("v"); var o = c.Arr("out");
            var (x, y) = m.Modify(v[0], v[1]);
            Close(o[0], x); Close(o[1], y);
        }
    }

    [Fact]
    public void Evaluate_MatchesClosedForm()
    {
        foreach (var c in Golden.Root.GetProperty("closed_form").EnumerateArray())
        {
            var m = new RawAccelModel(WholeFromGolden(c.GetProperty("p")) with { DegreesRotation = 0 });
            var th = c.Arr("theta"); var g = c.Arr("gain"); var psi = c.Arr("psi");
            for (int i = 0; i < th.Length; i++)
            {
                var (gg, pp) = m.Evaluate(th[i]);
                Close(g[i], gg, 1e-12);
                Assert.True(Math.Abs(RawAccelModel.WrapAngle(pp - psi[i])) < 1e-12);
            }
        }
    }

    [Fact]
    public void DefaultProfile_IsIdentity()
    {
        var m = new RawAccelModel(RawAccelProfile.Default);
        foreach (var t in Golden.Theta(64))
        {
            var (g, psi) = m.Evaluate(t, 7.3);
            Close(1, g); Assert.True(Math.Abs(RawAccelModel.WrapAngle(psi - t)) < 1e-12);
        }
    }

    [Fact]
    public void RangeWeights_HaveNoEffectWithNoAccel()
    {
        var m = new RawAccelModel(new RawAccelProfile { RangeWeightX = 0.2, RangeWeightY = 3 });
        Close(1, m.Evaluate(0.7).Gain);
    }

    [Fact]
    public void OutputDpiNormalisation_UsesDeviceDpi()
    {
        // device DPI 1600 -> dpi_factor 0.625; Output DPI 1600 -> output 1:1 (GUI semantics of "Output DPI")
        double f = RawAccelModel.DpiFactorForDeviceDpi(1600);
        Close(0.625, f);
        Close(1.0, RawAccelModel.DpiFactorForDeviceDpi(0));
        var m = new RawAccelModel(new RawAccelProfile { OutputDpi = 1600 });
        var (x, y) = m.Modify(10, -4, f);
        Close(10, x); Close(-4, y);
    }

    [Fact]
    public void Snap_SnapsNearAxisInputs()
    {
        var m = new RawAccelModel(new RawAccelProfile { DegreesSnap = 10 });
        var (x, y) = m.Modify(10, 1); // 5.7° < 10° -> horizontal, magnitude kept
        Close(Math.Sqrt(101), x); Assert.Equal(0, y);
        (x, y) = m.Modify(-1, -10);
        Assert.Equal(0, x); Close(-Math.Sqrt(101), y);
        (x, y) = m.Modify(5, 5);
        Close(5, x); Close(5, y);
    }

    [Fact]
    public void SpeedCap_ClampsMagnitude()
    {
        var m = new RawAccelModel(new RawAccelProfile { SpeedMax = 5 });
        var (x, y) = m.Modify(30, 40, 1, 1); // speed 50 -> 5
        Close(3, x); Close(4, y);
    }

    [Fact]
    public void Lookup_VelocityAndSensitivityModes()
    {
        var sens = new AccelCurve { Mode = AccelMode.Lut, Gain = false, Data = new float[] { 1, 2, 3, 4 } };
        Close(2, RawAccelModel.Lookup(sens, 0.5));   // below first point -> first y
        Close(3, RawAccelModel.Lookup(sens, 2));     // interpolated
        // beyond the last point lookup::operator() extrapolates linearly (lerp with t > 1 keeps max(x, b))
        Close(11, RawAccelModel.Lookup(sens, 10));
        Assert.Equal(0, RawAccelModel.Lookup(sens, 0));
        var vel = sens with { Gain = true };
        Close(3.0 / 2, RawAccelModel.Lookup(vel, 2));
        Close(2.0 / 1, RawAccelModel.Lookup(vel, 0.5));
        Close(4.0 / 3, RawAccelModel.Lookup(vel, 3));
    }

    [Fact]
    public void LpNormAndDomainWeights_ChangeSpeedOnly()
    {
        // velocity LUT y = s^2 => sensitivity = s ; whole mode
        var curve = new AccelCurve { Mode = AccelMode.Lut, Gain = true, Data = new float[] { 1, 1, 100, 10000 } };
        var euclid = new RawAccelModel(new RawAccelProfile { AccelX = curve });
        var maxNorm = new RawAccelModel(new RawAccelProfile { AccelX = curve, LpNorm = 16 });
        var (x1, _) = euclid.Modify(3, 4); // speed 5, sens = 1 + (lerp/5 - 1)
        var (x2, _) = maxNorm.Modify(3, 4); // speed 4
        Assert.NotEqual(x1, x2);
        Assert.Throws<NotSupportedException>(() => new RawAccelModel(new RawAccelProfile { AccelX = new AccelCurve { Mode = AccelMode.Classic } }));
    }

    [Fact]
    public void LeftUpMultipliers_ApplyToNegativeComponents()
    {
        var m = new RawAccelModel(new RawAccelProfile { LrOutputDpiRatio = 1.2, UdOutputDpiRatio = 0.8, YxOutputDpiRatio = 1.1 });
        var (x, y) = m.Modify(-10, -10);
        Close(-12, x); Close(-8.8, y);
        (x, y) = m.Modify(10, 10);
        Close(10, x); Close(11, y);
    }
}
