using OpenVector.RawAccel.Model;

namespace OpenVector.RawAccel.Settings;

public enum NeutralMode
{
    /// <summary>D4: Output DPI = the user's prior value, Y/X = L/R = U/D = 1, OpenVector's LUT removed.</summary>
    Identity,
    /// <summary>Restore the owned fields exactly as they were before OpenVector's first apply (if known).</summary>
    RestoreBaseline,
}

/// <summary>Generates the neutral ("reset") owned values for a profile.</summary>
public static class NeutralProfile
{
    /// <param name="current">Owned values currently in the file.</param>
    /// <param name="baseline">The user's pre-OpenVector owned values, if OpenVector recorded them.</param>
    public static OwnedProfileValues Compute(OwnedProfileValues current, OwnedProfileValues? baseline, NeutralMode mode = NeutralMode.Identity)
    {
        if (mode == NeutralMode.RestoreBaseline && baseline is not null) return baseline;

        var neutral = current with
        {
            OutputDpi = baseline?.OutputDpi ?? current.OutputDpi,
            YxRatio = 1, LrRatio = 1, UdRatio = 1,
        };
        // Only undo the shape (LUT + range weights) if it is OpenVector's constant LUT; never touch a user curve.
        if (RawAccelParameterMapping.IsOpenVectorLut(current.AccelX))
        {
            double c = current.AccelX.Data[1];
            neutral = neutral with
            {
                AccelX = baseline?.AccelX is { Mode: AccelMode.NoAccel } b ? b : AccelCurve.NoAccel,
                RangeWeightX = baseline?.RangeWeightX ?? 1,
                RangeWeightY = baseline?.RangeWeightY ?? 1,
                Whole = baseline?.Whole ?? current.Whole,
                // without a recorded baseline, keep the effective base gain: G0 was divided by k(0) = c
                OutputDpi = baseline?.OutputDpi ?? current.OutputDpi * (1 + (c - 1) * current.RangeWeightX),
            };
        }
        return neutral;
    }
}
