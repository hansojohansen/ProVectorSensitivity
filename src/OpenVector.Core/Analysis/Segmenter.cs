using OpenVector.Core.Data;

namespace OpenVector.Core.Analysis;

/// <summary>Thresholds for <see cref="Segmenter"/>. Defaults are documented there and mirrored in
/// reference/python/openvector_ref/segment.py.</summary>
public sealed record SegmentationOptions
{
    public const string AlgorithmId = "ov-velocity-1";

    /// <summary>Resampling grid (µs).</summary>
    public int GridUs { get; init; } = 1000;
    /// <summary>Gaussian velocity smoothing SD (ms), kernel truncated at 3 SD.</summary>
    public double SmoothingSigmaMs { get; init; } = 5.0;
    /// <summary>Movement threshold as a fraction of peak speed.</summary>
    public double OnsetFraction { get; init; } = 0.05;
    /// <summary>Absolute floor of the movement threshold (counts/s).</summary>
    public double MinSpeedCountsPerS { get; init; } = 20.0;
    /// <summary>A local speed minimum splits submovements if below this × min(peak before, peak after).</summary>
    public double DipRatio { get; init; } = 0.6;
    /// <summary>An along-target velocity sign change splits submovements if speed is below this × peak so far.</summary>
    public double ReversalRatio { get; init; } = 0.5;
    /// <summary>Window for the "peak after" of the dip rule (ms).</summary>
    public double LookaheadMs { get; init; } = 100.0;
    /// <summary>Corrections shorter than this (ms) or smaller than MinSubmovementCounts are dropped.</summary>
    public double MinSubmovementMs { get; init; } = 8.0;
    public double MinSubmovementCounts { get; init; } = 1.0;
}

public sealed record Submovement(long TStartUs, long TEndUs, double Dx, double Dy, double PeakSpeed);

/// <summary>Positions are relative to the path origin (the position at stimulus), in counts.</summary>
public sealed record SegmentationResult(
    long TOnsetUs, long TPeakUs, long TPrimaryEndUs, long TEndUs, double PeakSpeed,
    double PrimaryEndX, double PrimaryEndY, double FinalEndX, double FinalEndY, double PathLength,
    IReadOnlyList<Submovement> Submovements)
{
    public int NCorrections => Submovements.Count - 1;
}

/// <summary>
/// Velocity-based submovement segmentation of a raw count stream (Meyer et al. 1988; research B §4.2).
/// <list type="number">
/// <item>Packets with identical timestamps (batch QPC) are merged; position = cumulative counts.</item>
/// <item>Position is linearly interpolated between packet times; a packet's counts are assumed to accrue over one
/// median packet interval before it (knots are inserted so idle gaps do not smear motion).</item>
/// <item>Resample on a 1 ms grid; central-difference velocity; Gaussian smoothing per axis (σ = 5 ms, edge
/// renormalised); speed = |v|.</item>
/// <item>Threshold = max(5 % of peak speed, 20 counts/s). Onset = first sample of the above-threshold run holding the peak.</item>
/// <item>A submovement ends when speed drops below threshold; or at a local minimum below 0.6 × min(peak so far,
/// peak in the next 100 ms); or when the along-target velocity changes sign while speed &lt; 0.5 × peak so far.</item>
/// <item>Segments before the global peak are merged into the primary; later ones &lt; 8 ms or &lt; 1 count are dropped.</item>
/// </list>
/// Results match the Python reference (golden vectors). At ≤125 Hz polling, consider a larger σ.
/// </summary>
public static class Segmenter
{
    public static SegmentationResult? Segment(
        ReadOnlySpan<long> tUs, ReadOnlySpan<int> dx, ReadOnlySpan<int> dy,
        long? tRefUs = null, double? direction = null, SegmentationOptions? options = null)
    {
        var o = options ?? new SegmentationOptions();
        if (tUs.Length == 0) return null;
        if (dx.Length != tUs.Length || dy.Length != tUs.Length) throw new ArgumentException("Path arrays differ in length.");

        var (tau, x, y) = Resample(tUs, dx, dy, tRefUs, o.GridUs);
        int n = tau.Length;
        if (n < 3) return null;

        double dt = o.GridUs * 1e-6;
        var vx = Gradient(x, dt);
        var vy = Gradient(y, dt);
        double sigma = o.SmoothingSigmaMs * 1000.0 / o.GridUs;
        vx = Smooth(vx, sigma);
        vy = Smooth(vy, sigma);
        var s = new double[n];
        for (int k = 0; k < n; k++) s[k] = Hypot(vx[k], vy[k]);
        double dir = direction ?? Math.Atan2(y[n - 1], x[n - 1]);
        double ux = Math.Cos(dir), uy = Math.Sin(dir);
        var va = new double[n];
        for (int k = 0; k < n; k++) va[k] = vx[k] * ux + vy[k] * uy;

        int kp = 0;
        for (int k = 1; k < n; k++) if (s[k] > s[kp]) kp = k;
        double peak = s[kp];
        if (peak <= o.MinSpeedCountsPerS) return null;
        double thr = Math.Max(o.OnsetFraction * peak, o.MinSpeedCountsPerS);
        int look = (int)Math.Round(o.LookaheadMs * 1000.0 / o.GridUs);

        int k0 = kp;
        while (k0 > 0 && s[k0 - 1] >= thr) k0--;

        double RightPeak(int k)
        {
            double m = s[k];
            for (int j = k + 1; j < n && j <= k + look && s[j] >= thr; j++)
                if (s[j] > m) m = s[j];
            return m;
        }

        var segs = new List<(int Start, int End, double Peak)>();
        bool inMove = true;
        int segStart = k0;
        double segPeak = s[k0];
        for (int k = k0 + 1; k < n; k++)
        {
            if (inMove)
            {
                if (s[k] < thr)
                {
                    segs.Add((segStart, k, segPeak));
                    inMove = false;
                    continue;
                }
                bool boundary = false;
                if (k + 1 < n && s[k] <= s[k - 1] && s[k] < s[k + 1] &&
                    s[k] < o.DipRatio * Math.Min(segPeak, RightPeak(k)))
                    boundary = true;
                if (!boundary && ((va[k - 1] > 0 && va[k] <= 0) || (va[k - 1] < 0 && va[k] >= 0)) &&
                    s[k] < o.ReversalRatio * segPeak)
                    boundary = true;
                if (boundary)
                {
                    segs.Add((segStart, k, segPeak));
                    segStart = k;
                    segPeak = s[k];
                }
                else if (s[k] > segPeak) segPeak = s[k];
            }
            else if (s[k] >= thr)
            {
                inMove = true;
                segStart = k;
                segPeak = s[k];
            }
        }
        if (inMove) segs.Add((segStart, n - 1, segPeak));

        int p = 0;
        while (p < segs.Count && segs[p].End < kp) p++;
        double primaryPeak = 0;
        for (int i = 0; i <= p; i++) primaryPeak = Math.Max(primaryPeak, segs[i].Peak);
        var kept = new List<(int Start, int End, double Peak)> { (segs[0].Start, segs[p].End, primaryPeak) };
        double minLen = o.MinSubmovementMs * 1000.0 / o.GridUs;
        for (int i = p + 1; i < segs.Count; i++)
        {
            var (a, b, pk) = segs[i];
            if (b - a < minLen || Hypot(x[b] - x[a], y[b] - y[a]) < o.MinSubmovementCounts) continue;
            kept.Add((a, b, pk));
        }

        var subs = kept.Select(k => new Submovement(tau[k.Start], tau[k.End], x[k.End] - x[k.Start], y[k.End] - y[k.Start], k.Peak)).ToArray();
        int pe = kept[0].End, last = kept[^1].End;
        double pathLength = 0;
        for (int k = 1; k < n; k++) pathLength += Hypot(x[k] - x[k - 1], y[k] - y[k - 1]);
        return new SegmentationResult(tau[k0], tau[kp], tau[pe], tau[last], peak,
            x[pe], y[pe], x[last], y[last], pathLength, subs);
    }

    /// <summary>Segment a trial's inline path: packets up to the final click, stimulus as time origin,
    /// target direction for the reversal rule. Null if the trial has no path or no movement.</summary>
    public static SegmentationResult? Segment(MovementTrial trial, SegmentationOptions? options = null)
    {
        if (trial.Path is not { } path || path.TUs.Length == 0) return null;
        long end = trial.Timing.TFinalClickUs ?? long.MaxValue;
        int m = 0;
        while (m < path.TUs.Length && path.TUs[m] <= end) m++;
        var g = trial.Geometry;
        double dir = Angles.Direction(g.Target.XCounts - g.Start.XCounts, g.Target.YCounts - g.Start.YCounts);
        return Segment(path.TUs.AsSpan(0, m), path.Dx.AsSpan(0, m), path.Dy.AsSpan(0, m), trial.Timing.TStimulusUs, dir, options);
    }

    /// <summary>
    /// Return a copy of the trial with the derived fields filled from its path: primary endpoint, onset/peak/
    /// primary-end times, reaction/movement/primary durations, peak speed, path length, submovements and errors.
    /// Trials without detectable movement are marked <see cref="ExcludedReason.SegmentationAmbiguous"/>.
    /// </summary>
    public static MovementTrial Annotate(MovementTrial trial, SegmentationOptions? options = null)
    {
        var seg = Segment(trial, options);
        if (seg is null)
            return trial with { ExcludedReason = trial.ExcludedReason ?? Data.ExcludedReason.SegmentationAmbiguous };
        var g = trial.Geometry;
        var t = trial.Timing;
        var primary = CountPoint.At(g.Start.XCounts + seg.PrimaryEndX, g.Start.YCounts + seg.PrimaryEndY);
        bool onScreen = t.TStimulusOnScreenUs is not null;
        long rtRef = t.TStimulusOnScreenUs ?? t.TStimulusUs;
        double degPerCount = trial.Mapping.YawDegPerCount;
        var outcome = trial.Outcome with { EndpointPrimary = primary };
        outcome = outcome with { Errors = TrialMetrics.ComputeErrors(trial with { Outcome = outcome }) };
        return trial with
        {
            Outcome = outcome,
            Timing = t with
            {
                TOnsetUs = seg.TOnsetUs,
                TPeakVelocityUs = seg.TPeakUs,
                TPrimaryEndUs = seg.TPrimaryEndUs,
                ReactionTimeUs = seg.TOnsetUs - rtRef,
                ReactionTimeBasis = onScreen ? ReactionTimeBasis.OnScreen : ReactionTimeBasis.Requested,
                MovementTimeUs = t.TFinalClickUs is { } click ? click - seg.TOnsetUs : null,
                PrimaryDurationUs = seg.TPrimaryEndUs - seg.TOnsetUs,
            },
            Kinematics = trial.Kinematics with
            {
                PeakSpeedCountsPerS = seg.PeakSpeed,
                PeakSpeedDegPerS = seg.PeakSpeed * degPerCount,
                PathLengthCounts = seg.PathLength,
                NCorrections = seg.NCorrections,
                Submovements = seg.Submovements.Select(sm => new SubmovementRecord
                {
                    TStartUs = sm.TStartUs, TEndUs = sm.TEndUs, DxCounts = sm.Dx, DyCounts = sm.Dy,
                    PeakSpeedCountsPerS = sm.PeakSpeed,
                }).ToArray(),
                Segmentation = SegmentationOptions.AlgorithmId,
            },
        };
    }

    private static (long[] Tau, double[] X, double[] Y) Resample(
        ReadOnlySpan<long> tUs, ReadOnlySpan<int> dx, ReadOnlySpan<int> dy, long? tRef, int gridUs)
    {
        var ts = new List<long>(tUs.Length);
        var xs = new List<long>(tUs.Length);
        var ys = new List<long>(tUs.Length);
        for (int i = 0; i < tUs.Length; i++)
        {
            if (ts.Count > 0 && tUs[i] == ts[^1]) { xs[^1] += dx[i]; ys[^1] += dy[i]; }
            else { ts.Add(tUs[i]); xs.Add(dx[i]); ys.Add(dy[i]); }
        }
        int n = ts.Count;
        long dtMed = 1000;
        if (n >= 2)
        {
            var diffs = new double[n - 1];
            for (int i = 1; i < n; i++) diffs[i - 1] = ts[i] - ts[i - 1];
            dtMed = (long)Math.Round(Stats.Median(diffs), MidpointRounding.ToEven);
        }
        dtMed = Math.Max(dtMed, 1);

        var kt = new List<long>();
        var kx = new List<double>();
        var ky = new List<double>();
        long first = ts[0] - dtMed;
        if (tRef is { } r && r < first) { kt.Add(r); kx.Add(0); ky.Add(0); }
        kt.Add(first); kx.Add(0); ky.Add(0);
        long cx = 0, cy = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && ts[i] - ts[i - 1] > 2 * dtMed) { kt.Add(ts[i] - dtMed); kx.Add(cx); ky.Add(cy); }
            cx += xs[i]; cy += ys[i];
            kt.Add(ts[i]); kx.Add(cx); ky.Add(cy);
        }

        long g0 = kt[0];
        int count = (int)Math.Ceiling((double)(kt[^1] - g0) / gridUs) + 1;
        var tau = new long[count];
        var x = new double[count];
        var y = new double[count];
        int j = 0;
        for (int k = 0; k < count; k++)
        {
            tau[k] = g0 + (long)gridUs * k;
            double tv = tau[k];
            while (j < kt.Count - 2 && kt[j + 1] <= tv) j++;
            if (tv >= kt[^1]) { x[k] = kx[^1]; y[k] = ky[^1]; continue; }
            double t0 = kt[j], t1 = kt[j + 1];
            double f = tv - t0;
            x[k] = (kx[j + 1] - kx[j]) / (t1 - t0) * f + kx[j];
            y[k] = (ky[j + 1] - ky[j]) / (t1 - t0) * f + ky[j];
        }
        return (tau, x, y);
    }

    private static double[] Gradient(double[] f, double dt)
    {
        int n = f.Length;
        var g = new double[n];
        g[0] = (f[1] - f[0]) / dt;
        g[n - 1] = (f[n - 1] - f[n - 2]) / dt;
        for (int i = 1; i < n - 1; i++) g[i] = (f[i + 1] - f[i - 1]) / (2.0 * dt);
        return g;
    }

    private static double[] Smooth(double[] v, double sigma)
    {
        int r = (int)Math.Ceiling(3.0 * sigma);
        var w = new double[2 * r + 1];
        for (int j = -r; j <= r; j++) w[j + r] = Math.Exp(-0.5 * (j / sigma) * (j / sigma));
        int n = v.Length;
        var o = new double[n];
        for (int k = 0; k < n; k++)
        {
            double num = 0, den = 0;
            for (int j = Math.Max(-r, -k); j <= Math.Min(r, n - 1 - k); j++)
            {
                num += w[j + r] * v[k + j];
                den += w[j + r];
            }
            o[k] = num / den;
        }
        return o;
    }

    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
}
