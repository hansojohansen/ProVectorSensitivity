using OpenVector.Core.Data;

namespace OpenVector.Core.Analysis;

/// <summary>
/// Per-trial metrics. Errors are fractions of the amplitude A (count space): along = (P−S)·u/A − 1
/// (&gt;0 overshoot), across = (P−S)·n/A with n = u rotated +π/2 in θ. OvershootRatio = 1 + along_primary.
/// MovementTime = final click − onset; PrimaryDuration = primary end − onset;
/// PathEfficiency = |final − start| / path length (1 = straight line).
/// </summary>
public sealed record TrialMetrics(
    double Theta, double AmplitudeCounts, int AmplitudeIndex, double Weight,
    double? AlongPrimary, double? AcrossPrimary, double? AlongFinal, double? AcrossFinal, double? OvershootRatio,
    long? MovementTimeUs, long? PrimaryDurationUs, double? PathEfficiency, int? NCorrections,
    double? PeakSpeedCountsPerS, long? ReactionTimeUs)
{
    /// <summary>Metrics for one trial, or null if it is excluded or has no geometry. A trial with a path but
    /// no primary endpoint is segmented first (<see cref="Segmenter.Annotate"/>).</summary>
    public static TrialMetrics? Compute(MovementTrial trial)
    {
        if (trial.Outcome.EndpointPrimary is null && trial.Path is not null)
            trial = Segmenter.Annotate(trial);
        if (trial.ExcludedReason is not null) return null;
        var g = trial.Geometry;
        double vx = g.Target.XCounts - g.Start.XCounts, vy = g.Target.YCounts - g.Start.YCounts;
        double amp = Math.Sqrt(vx * vx + vy * vy);
        if (!(amp > 0)) return null;

        var e = ComputeErrors(trial);
        var t = trial.Timing;
        var k = trial.Kinematics;
        long? mt = t.TOnsetUs is { } on && t.TFinalClickUs is { } click ? click - on : t.MovementTimeUs;
        long? pd = t.TOnsetUs is { } on2 && t.TPrimaryEndUs is { } pe ? pe - on2 : t.PrimaryDurationUs;
        double? eff = null;
        if (trial.Outcome.EndpointFinal is { } f && k.PathLengthCounts is { } pl && pl != 0)
        {
            double fx = f.XCounts - g.Start.XCounts, fy = f.YCounts - g.Start.YCounts;
            eff = Math.Sqrt(fx * fx + fy * fy) / pl;
        }
        return new TrialMetrics(Angles.Direction(vx, vy), amp, trial.Condition?.AmplitudeIndex ?? 0,
            trial.Provenance.Confidence, e.AlongPrimary, e.AcrossPrimary, e.AlongFinal, e.AcrossFinal,
            e.AlongPrimary is { } ap ? 1.0 + ap : null, mt, pd, eff, k.NCorrections, k.PeakSpeedCountsPerS,
            t.ReactionTimeUs);
    }

    public static TrialErrors ComputeErrors(MovementTrial trial)
    {
        var g = trial.Geometry;
        (double, double)? Of(CountPoint? p) => p is null ? null
            : Angles.AlongAcross(g.Start.XCounts, g.Start.YCounts, g.Target.XCounts, g.Target.YCounts, p.XCounts, p.YCounts);
        var pr = Of(trial.Outcome.EndpointPrimary);
        var fi = Of(trial.Outcome.EndpointFinal);
        return new TrialErrors
        {
            AlongPrimary = pr?.Item1, AcrossPrimary = pr?.Item2, AlongFinal = fi?.Item1, AcrossFinal = fi?.Item2,
        };
    }
}
