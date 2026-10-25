namespace OpenVector.RawAccel.Model;

/// <summary>Raw Accel 1.7 accel curve modes (JSON enum names from wrapper.cpp <c>AccelMode</c>).</summary>
public enum AccelMode { Classic, Jump, Natural, Synchronous, Power, Lut, NoAccel }

/// <summary>
/// The part of a Raw Accel <c>accel_args</c> the model can evaluate: <c>noaccel</c> and <c>lut</c>.
/// Other modes are carried as <see cref="Mode"/> only; evaluating them throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed record AccelCurve
{
    public AccelMode Mode { get; init; } = AccelMode.NoAccel;

    /// <summary>JSON "Gain / Velocity". For LUT: true = y is velocity, false = y is sensitivity.</summary>
    public bool Gain { get; init; } = true;

    /// <summary>LUT points as flat (x1, y1, x2, y2, ...). Stored as float32 by the driver.</summary>
    public IReadOnlyList<float> Data { get; init; } = Array.Empty<float>();

    public static AccelCurve NoAccel { get; } = new();

    /// <summary>A 2-point sensitivity LUT f(s) ≡ c (A §2.4 step 4).</summary>
    public static AccelCurve ConstantSensitivity(double c) =>
        new() { Mode = AccelMode.Lut, Gain = false, Data = new[] { 1f, (float)c, 2f, (float)c } };

    /// <summary>True if this is a sensitivity LUT whose y values are all equal (f ≡ c).</summary>
    public bool IsConstantSensitivityLut(out double c)
    {
        c = double.NaN;
        if (Mode != AccelMode.Lut || Gain || Data.Count < 4) return false;
        float y0 = Data[1];
        for (int i = 3; i < Data.Count; i += 2) if (Data[i] != y0) return false;
        c = y0;
        return true;
    }
}

/// <summary>
/// The subset of a Raw Accel 1.7 <c>profile</c> (common/rawaccel-base.hpp) that OpenVector models.
/// Defaults equal Raw Accel's defaults. Smoothing (EMA "coalescion") is stateful and not modelled.
/// </summary>
public sealed record RawAccelProfile
{
    public double OutputDpi { get; init; } = 1000;
    public double YxOutputDpiRatio { get; init; } = 1;
    public double LrOutputDpiRatio { get; init; } = 1;
    public double UdOutputDpiRatio { get; init; } = 1;
    public double DegreesRotation { get; init; }
    public double DegreesSnap { get; init; }
    public double RangeWeightX { get; init; } = 1;
    public double RangeWeightY { get; init; } = 1;
    public double DomainWeightX { get; init; } = 1;
    public double DomainWeightY { get; init; } = 1;
    /// <summary>JSON "Whole/combined accel ..." (false = by-component mode).</summary>
    public bool Whole { get; init; } = true;
    public double LpNorm { get; init; } = 2;
    public double SpeedMin { get; init; }
    /// <summary>JSON "Input Speed Cap".</summary>
    public double SpeedMax { get; init; }
    public AccelCurve AccelX { get; init; } = AccelCurve.NoAccel;
    public AccelCurve AccelY { get; init; } = AccelCurve.NoAccel;

    public static RawAccelProfile Default { get; } = new();
}
