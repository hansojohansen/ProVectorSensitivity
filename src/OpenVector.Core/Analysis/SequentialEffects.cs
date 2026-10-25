using OpenVector.Core.Data;
using OpenVector.Core.Profiles;

namespace OpenVector.Core.Analysis;

public sealed record GroupStats(int N, double? Mean, double? Se);
public sealed record DeltaBinStats(double Delta, int N, double? Mean, double? Se);
public sealed record ReactionTimeStats(int Sector, double Theta, int N, double? MeanUs, double? MedianUs);
public sealed record Difference(double Diff, double Se);

public sealed record SequentialEffectsResult(
    GroupStats Same, GroupStats Orthogonal, GroupStats Reversal, Difference? ReversalMinusSame,
    IReadOnlyList<DeltaBinStats> ByDelta, IReadOnlyList<ReactionTimeStats> ReactionTime);

public sealed record SequentialOptions
{
    public double SameMaxDeg { get; init; } = 45.0;
    public double ReversalMinDeg { get; init; } = 135.0;
    public int DeltaBins { get; init; } = 8;
    public int ReactionTimeSectors { get; init; } = 8;
    public double MaxAbsAlongError { get; init; } = 0.5;
}

/// <summary>
/// Sequential effects and per-direction reaction time (D15). REPORTED ONLY: a static profile cannot apply them,
/// because the driver does not know the previous flick's intent. Mirrors reference/python/openvector_ref/sequential.py.
/// <para>For each used trial with <c>context.previous</c>: Δ = θ − θ_prev wrapped to [−π, π]; class = same (|Δ| &lt; 45°),
/// reversal (|Δ| &gt; 135°) or orthogonal; residual = along_primary − b_model(θ). Subtracting the static model first
/// keeps direction imbalance between classes from looking like a sequential effect. Reports per-class
/// n/mean/SE, reversal − same (Welch SE), and the mean residual in 8 signed-Δ bins centred on 0°, 45°, ….</para>
/// <para>Reaction time per direction sector uses <c>timing.reaction_time_us</c> (from the first on-screen frame
/// when the trainer logged it; see <c>reaction_time_basis</c>).</para>
/// </summary>
public static class SequentialEffects
{
    public static SequentialEffectsResult Compute(IEnumerable<MovementTrial> trials, GainModel? model = null, SequentialOptions? options = null)
    {
        var o = options ?? new SequentialOptions();
        var groups = new[] { new List<double>(), new List<double>(), new List<double>() }; // same, orthogonal, reversal
        var dbins = Enumerable.Range(0, o.DeltaBins).Select(_ => new List<double>()).ToArray();
        var rts = Enumerable.Range(0, o.ReactionTimeSectors).Select(_ => new List<double>()).ToArray();
        foreach (var r in trials)
        {
            if (r.ExcludedReason is not null) continue;
            var g = r.Geometry;
            double theta = Angles.Direction(g.Target.XCounts - g.Start.XCounts, g.Target.YCounts - g.Start.YCounts);
            if (r.Timing.ReactionTimeUs is { } rt && rt > 0) rts[Angles.SectorOf(theta, o.ReactionTimeSectors)].Add(rt);
            var prev = r.Context?.Previous;
            var p = r.Outcome.EndpointPrimary;
            if (prev is null || p is null) continue;
            var (along, _) = Angles.AlongAcross(g.Start.XCounts, g.Start.YCounts, g.Target.XCounts, g.Target.YCounts, p.XCounts, p.YCounts);
            if (Math.Abs(along) > o.MaxAbsAlongError) continue;
            double resid = along - (model is null ? 0.0 : FourierSeries.Evaluate(model.Coeffs, theta));
            double delta = Angles.Delta(theta, prev.DirectionTheta);
            double ad = Math.Abs(delta) * (180.0 / Math.PI);
            int cls = ad < o.SameMaxDeg ? 0 : ad > o.ReversalMinDeg ? 2 : 1;
            groups[cls].Add(resid);
            dbins[Angles.SectorOf(delta, o.DeltaBins)].Add(resid);
        }
        var same = Summ(groups[0]);
        var orth = Summ(groups[1]);
        var rev = Summ(groups[2]);
        Difference? diff = rev.Se is { } rse && same.Se is { } sse
            ? new Difference(rev.Mean!.Value - same.Mean!.Value, Math.Sqrt(rse * rse + sse * sse)) : null;
        var byDelta = dbins.Select((v, k) =>
        {
            var s = Summ(v);
            return new DeltaBinStats(Angles.SectorCenter(k, o.DeltaBins), s.N, s.Mean, s.Se);
        }).ToArray();
        var rtStats = rts.Select((v, k) => new ReactionTimeStats(k, Angles.SectorCenter(k, o.ReactionTimeSectors), v.Count,
            v.Count > 0 ? Stats.Mean(v) : null, v.Count > 0 ? Stats.Median(v) : null)).ToArray();
        return new SequentialEffectsResult(same, orth, rev, diff, byDelta, rtStats);
    }

    private static GroupStats Summ(List<double> v) =>
        new(v.Count, v.Count > 0 ? Stats.Mean(v) : null, v.Count >= 2 ? Stats.StdDev(v) / Math.Sqrt(v.Count) : null);
}
