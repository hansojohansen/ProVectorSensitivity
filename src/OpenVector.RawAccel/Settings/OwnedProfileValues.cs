using OpenVector.RawAccel.Model;

namespace OpenVector.RawAccel.Settings;

/// <summary>
/// The fields of one Raw Accel profile that OpenVector owns (D4): Output DPI, Y/X, L/R, U/D, range weights,
/// and — only when the diagonal shape κ is used — the whole-mode flag and the horizontal accel curve
/// (a constant-sensitivity LUT). Everything else in settings.json is never modified.
/// </summary>
public sealed record OwnedProfileValues
{
    public double OutputDpi { get; init; } = 1000;
    public double YxRatio { get; init; } = 1;
    public double LrRatio { get; init; } = 1;
    public double UdRatio { get; init; } = 1;
    public double RangeWeightX { get; init; } = 1;
    public double RangeWeightY { get; init; } = 1;
    public bool Whole { get; init; } = true;
    public AccelCurve AccelX { get; init; } = AccelCurve.NoAccel;

    /// <summary>Equality with a relative tolerance (JSON round-trips may change the last digit).</summary>
    public bool ApproximatelyEquals(OwnedProfileValues o, double tol = 1e-9)
    {
        static bool Eq(double a, double b, double t) => Math.Abs(a - b) <= t * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Eq(OutputDpi, o.OutputDpi, tol) && Eq(YxRatio, o.YxRatio, tol) && Eq(LrRatio, o.LrRatio, tol) &&
               Eq(UdRatio, o.UdRatio, tol) && Eq(RangeWeightX, o.RangeWeightX, tol) && Eq(RangeWeightY, o.RangeWeightY, tol) &&
               Whole == o.Whole && AccelX.Mode == o.AccelX.Mode && AccelX.Gain == o.AccelX.Gain &&
               AccelX.Data.SequenceEqual(o.AccelX.Data);
    }
}

/// <summary>Maps reduced projection parameters onto Raw Accel fields (A §2.4 step 4).</summary>
public static class RawAccelParameterMapping
{
    /// <summary>LUT constant used for κ (A §2.4: c = max(2, 1/κ + ε)). 2 is exact in float32.</summary>
    public static double LutConstantFor(double kappa) => Math.Max(2.0, 1.0 / kappa + 0.01);

    /// <summary>True if this accel curve is the constant LUT signature OpenVector writes: data [1, c, 2, c], gain = false.</summary>
    public static bool IsOpenVectorLut(AccelCurve c) =>
        c.Mode == AccelMode.Lut && !c.Gain && c.Data.Count == 4 && c.Data[0] == 1f && c.Data[2] == 2f && c.Data[1] == c.Data[3];

    /// <summary>
    /// Composes relative parameters with the user's baseline owned values. ρ, L, U multiply the baseline
    /// ratios; G0 scales the baseline Output DPI (divided by k(0) of the LUT when κ ≠ 1).
    /// </summary>
    public static OwnedProfileValues Compose(OwnedProfileValues baseline, Projection.DirectionalParams p, double kappaEpsilon = 1e-6)
    {
        bool useShape = Math.Abs(p.Kappa - 1) > kappaEpsilon;
        if (useShape && !BaselineAllowsShape(baseline, null))
            throw new InvalidOperationException("κ ≠ 1 requires a baseline without an accel curve.");
        var o = baseline with
        {
            YxRatio = baseline.YxRatio * p.Rho,
            LrRatio = baseline.LrRatio * p.L,
            UdRatio = baseline.UdRatio * p.U,
        };
        if (!useShape) return o with { OutputDpi = baseline.OutputDpi * p.G0 };
        double c = (float)LutConstantFor(p.Kappa);       // the driver stores LUT data as float
        double rx = 1, ry = (p.Kappa * c - 1) / (c - 1);
        double k0 = 1 + (c - 1) * rx;                    // = c
        return o with
        {
            OutputDpi = baseline.OutputDpi * p.G0 / k0,
            Whole = true,
            AccelX = AccelCurve.ConstantSensitivity(c),
            RangeWeightX = rx,
            RangeWeightY = ry,
        };
    }

    /// <summary>κ needs a directional-free baseline: no accel curve in the curves Raw Accel actually uses.</summary>
    public static bool BaselineAllowsShape(OwnedProfileValues baseline, AccelCurve? baselineAccelY) =>
        baseline.AccelX.Mode == AccelMode.NoAccel && (baseline.Whole || baselineAccelY is null || baselineAccelY.Mode == AccelMode.NoAccel);
}
