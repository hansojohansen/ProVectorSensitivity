using OpenVector.Core.Data;
using OpenVector.Core.Profiles;

namespace OpenVector.Core.Analysis;

public enum FitSignal { Primary, Final }
public enum OrderRule { MinCv, OneStandardError }

public sealed record GainFitOptions
{
    public int MaxOrder { get; init; } = 8;
    /// <summary>Roughness penalty λ in λ·Σ k⁴(a_k² + b_k²), in units of trial weight.</summary>
    public double Lambda { get; init; } = 0.05;
    public int Strata { get; init; } = 32;
    public double MinTrialsPerStratum { get; init; } = 20.0;
    public int NeighbourReach { get; init; } = 2;
    public int Folds { get; init; } = 10;
    public GainBounds Bounds { get; init; } = GainBounds.Default;
    /// <summary>Trials with |along error| above this are treated as outliers (segmentation failures).</summary>
    public double MaxAbsAlongError { get; init; } = 0.5;
    public int BootstrapReps { get; init; } = 200;
    public ulong Seed { get; init; } = 12345;
    public int DisplayBins { get; init; } = 64;
    public FitSignal Signal { get; init; } = FitSignal.Primary;
    public OrderRule OrderRule { get; init; } = OrderRule.MinCv;
}

public sealed record GainFitResult
{
    public required int Order { get; init; }
    public required double[] Coeffs { get; init; }
    public required double[][] Cov { get; init; }
    public required double[] CvScores { get; init; }
    public required double[] CvSe { get; init; }
    public required double ResidualSd { get; init; }
    public required int NTrials { get; init; }
    public required int NUsed { get; init; }
    public required int[] SparseStrata { get; init; }
    public required DisplayBin[] Bins { get; init; }
    public required GainFitOptions Options { get; init; }

    public GainModel Model => new() { Order = Order, Coeffs = Coeffs, Cov = Cov };

    /// <summary>Assemble an immutable Profile v1 (no projections yet; backends add theirs).</summary>
    public Profile ToProfile(string id, DateTimeOffset createdAt, ProfileMapping mapping,
        IEnumerable<string> sessions, string? parentId = null, string? name = null) => new()
    {
        Id = id, ParentId = parentId, CreatedAt = createdAt, Name = name, Mapping = mapping, Model = Model,
        Bounds = Options.Bounds, Bins = Bins, Sessions = sessions.ToArray(),
        Fit = new FitDiagnostics
        {
            Method = GainMapEstimator.MethodId, Signal = Options.Signal == FitSignal.Primary ? "primary" : "final",
            NTrials = NTrials, NUsed = NUsed, OrderSelected = Order, Lambda = Options.Lambda, ResidualSd = ResidualSd,
            Strata = Options.Strata, MinTrialsPerStratum = Options.MinTrialsPerStratum, SparseStrata = SparseStrata,
            CvScores = CvScores, BootstrapReps = Options.BootstrapReps, Seed = (long)Options.Seed,
        },
    };
}

/// <summary>
/// Estimates the static directional gain map g(θ) from calibration trials. Mirrors
/// reference/python/openvector_ref/fit.py (golden vectors); see that file for the full write-up.
/// <para><b>Signal.</b> e_i = relative along-axis error of the PRIMARY submovement endpoint, (P−S)·u/A − 1, at
/// θ_i = direction of target − start in counts. The primary submovement is open-loop, so it carries the gain error;
/// final endpoints are corrected onto the target and carry almost none.</para>
/// <para><b>Model.</b> b(θ) = E[e | θ] as a Fourier series of order K ≤ 8, by penalised weighted least squares
/// min Σ w_i (e_i − x_i·c)² + λ Σ_k k⁴ (a_k² + b_k²); w_i = provenance confidence; c0 unpenalised.</para>
/// <para><b>Compensating gain.</b> If the user's open-loop output in direction θ is (1 + b(θ)) × the intended
/// displacement, then scaling counts by g(θ) = 1/(1 + b(θ)) puts the expected primary endpoint on target.
/// Assumptions: (1) the bias is multiplicative in extent and does not depend on the gain itself (no motor
/// adaptation; users will adapt over time); (2) trials ran under identity gain, or the result is relative to the
/// active map (compose multiplicatively: g_new = g_active × g); (3) the range effect is averaged by the balanced
/// amplitude design and ends up in c0; (4) θ is the target direction (≈ movement direction); (5) clamped to bounds.</para>
/// <para><b>Sparse strata (D9).</b> For each of 32 sectors with summed weight n_s &lt; 20, add 20 − n_s pseudo-trials
/// at its centre with the 1/d-weighted mean of dense sectors within ±2, or 0 (gain 1.0) if none.</para>
/// <para><b>Order.</b> 10-fold CV (fold = used-trial index mod 10, pseudo-trials always in training), lowest CV
/// error (or the 1-SE rule). <b>CIs:</b> bootstrap over used trials (SplitMix64), fixed K, percentile 2.5/97.5.</para>
/// </summary>
public static class GainMapEstimator
{
    public const string MethodId = "fourier-pwls-v1";

    public sealed record Signal(double[] Theta, double[] Error, double[] Weight);

    /// <summary>Used trials, in input order: excluded, unknown-truth, zero-weight, endpoint-less and outlier trials are skipped.</summary>
    public static Signal ExtractSignal(IEnumerable<MovementTrial> trials, GainFitOptions? options = null)
    {
        var o = options ?? new GainFitOptions();
        var th = new List<double>();
        var ee = new List<double>();
        var ww = new List<double>();
        foreach (var r in trials)
        {
            if (r.ExcludedReason is not null) continue;
            if (r.Provenance.TargetTruth == TargetTruth.Unknown || r.Provenance.Confidence <= 0) continue;
            var p = o.Signal == FitSignal.Primary ? r.Outcome.EndpointPrimary : r.Outcome.EndpointFinal;
            if (p is null) continue;
            var g = r.Geometry;
            double vx = g.Target.XCounts - g.Start.XCounts, vy = g.Target.YCounts - g.Start.YCounts;
            if (!(Math.Sqrt(vx * vx + vy * vy) > 0)) continue;
            var (along, _) = Angles.AlongAcross(g.Start.XCounts, g.Start.YCounts, g.Target.XCounts, g.Target.YCounts, p.XCounts, p.YCounts);
            if (Math.Abs(along) > o.MaxAbsAlongError) continue;
            th.Add(Angles.Direction(vx, vy));
            ee.Add(along);
            ww.Add(r.Provenance.Confidence);
        }
        return new Signal(th.ToArray(), ee.ToArray(), ww.ToArray());
    }

    public static GainFitResult Fit(IReadOnlyCollection<MovementTrial> trials, GainFitOptions? options = null)
    {
        var o = options ?? new GainFitOptions();
        var s = ExtractSignal(trials, o);
        return Fit(s.Theta, s.Error, s.Weight, o, trials.Count);
    }

    public static GainFitResult Fit(double[] theta, double[] e, double[] w, GainFitOptions? options = null, int? nTrials = null)
    {
        var o = options ?? new GainFitOptions();
        int n = theta.Length;
        var (order, cv, cvSe) = CrossValidate(theta, e, w, o);
        var ps = PseudoObservations(theta, e, w, o);
        var (c, l) = Solve(Concat(theta, ps.Theta), Concat(e, ps.Error), Concat(w, ps.Weight), order, o.Lambda);

        int p = 2 * order + 1;
        Span<double> row = stackalloc double[p];
        double rss = 0, sw = 0;
        for (int i = 0; i < n; i++)
        {
            FourierSeries.DesignRow(theta[i], order, row);
            double r = e[i] - Dot(row, c);
            rss += w[i] * r * r;
            sw += w[i];
        }
        double sigma2 = rss / Math.Max(sw - p, 1.0);
        var cov = LinearAlgebra.Inverse(l);
        foreach (var rr in cov) for (int j = 0; j < rr.Length; j++) rr[j] *= sigma2;

        int nb = o.DisplayBins;
        var binTheta = Enumerable.Range(0, nb).Select(k => Angles.SectorCenter(k, nb)).ToArray();
        var binN = new int[nb];
        foreach (var t in theta) binN[Angles.SectorOf(t, nb)]++;
        var gain = Gains(c, binTheta, o.Bounds);
        var lo = (double[])gain.Clone();
        var hi = (double[])gain.Clone();
        if (o.BootstrapReps > 0 && n > 0)
        {
            var rng = new SplitMix64(o.Seed);
            var samples = new double[nb][];
            for (int k = 0; k < nb; k++) samples[k] = new double[o.BootstrapReps];
            var tb = new double[n];
            var eb = new double[n];
            var wb = new double[n];
            for (int rep = 0; rep < o.BootstrapReps; rep++)
            {
                for (int i = 0; i < n; i++)
                {
                    int idx = rng.NextInt(n);
                    tb[i] = theta[idx]; eb[i] = e[idx]; wb[i] = w[idx];
                }
                var pb = PseudoObservations(tb, eb, wb, o);
                var (cb, _) = Solve(Concat(tb, pb.Theta), Concat(eb, pb.Error), Concat(wb, pb.Weight), order, o.Lambda);
                var gb = Gains(cb, binTheta, o.Bounds);
                for (int k = 0; k < nb; k++) samples[k][rep] = gb[k];
            }
            for (int k = 0; k < nb; k++)
            {
                Array.Sort(samples[k]);
                lo[k] = Stats.PercentileSorted(samples[k], 2.5);
                hi[k] = Stats.PercentileSorted(samples[k], 97.5);
            }
        }

        return new GainFitResult
        {
            Order = order, Coeffs = c, Cov = cov, CvScores = cv, CvSe = cvSe, ResidualSd = Math.Sqrt(sigma2),
            NTrials = nTrials ?? n, NUsed = n, SparseStrata = ps.Sparse, Options = o,
            Bins = Enumerable.Range(0, nb).Select(k => new DisplayBin
            {
                Theta = binTheta[k], Gain = gain[k], CiLow = lo[k], CiHigh = hi[k], N = binN[k],
            }).ToArray(),
        };
    }

    private sealed record Pseudo(double[] Theta, double[] Error, double[] Weight, int[] Sparse);

    private static Pseudo PseudoObservations(double[] theta, double[] e, double[] w, GainFitOptions o)
    {
        int S = o.Strata;
        var nw = new double[S];
        var sw = new double[S];
        for (int i = 0; i < theta.Length; i++)
        {
            int s = Angles.SectorOf(theta[i], S);
            nw[s] += w[i];
            sw[s] += w[i] * e[i];
        }
        var pt = new List<double>();
        var pe = new List<double>();
        var pw = new List<double>();
        var sparse = new List<int>();
        for (int s = 0; s < S; s++)
        {
            if (nw[s] >= o.MinTrialsPerStratum) continue;
            double num = 0, den = 0;
            for (int d = 1; d <= o.NeighbourReach; d++)
                foreach (int side in (ReadOnlySpan<int>)[-1, 1])
                {
                    int q = ((s + side * d) % S + S) % S;
                    if (nw[q] >= o.MinTrialsPerStratum)
                    {
                        num += sw[q] / nw[q] / d;
                        den += 1.0 / d;
                    }
                }
            pt.Add(Angles.SectorCenter(s, S));
            pe.Add(den > 0 ? num / den : 0.0);
            pw.Add(o.MinTrialsPerStratum - nw[s]);
            sparse.Add(s);
        }
        return new Pseudo(pt.ToArray(), pe.ToArray(), pw.ToArray(), sparse.ToArray());
    }

    private static (int Order, double[] Cv, double[] Se) CrossValidate(double[] theta, double[] e, double[] w, GainFitOptions o)
    {
        int n = theta.Length, folds = o.Folds;
        int kmax = Math.Min(o.MaxOrder, Math.Max(0, (n / folds - 1) / 2));
        if (n < folds) return (0, [], []);
        var ps = PseudoObservations(theta, e, w, o);
        var cv = new double[kmax + 1];
        var se = new double[kmax + 1];
        for (int K = 0; K <= kmax; K++)
        {
            var scores = new double[folds];
            for (int f = 0; f < folds; f++)
            {
                var tr = Enumerable.Range(0, n).Where(i => i % folds != f).ToArray();
                var (c, _) = Solve(Concat(Pick(theta, tr), ps.Theta), Concat(Pick(e, tr), ps.Error),
                    Concat(Pick(w, tr), ps.Weight), K, o.Lambda);
                double num = 0, den = 0;
                var row = new double[2 * K + 1];
                for (int i = f; i < n; i += folds)
                {
                    FourierSeries.DesignRow(theta[i], K, row);
                    double r = e[i] - Dot(row, c);
                    num += w[i] * r * r;
                    den += w[i];
                }
                scores[f] = num / den;
            }
            cv[K] = Stats.Mean(scores);
            se[K] = Stats.StdDev(scores) / Math.Sqrt(folds);
        }
        int best = 0;
        for (int K = 1; K <= kmax; K++) if (cv[K] < cv[best]) best = K;
        if (o.OrderRule == OrderRule.OneStandardError)
        {
            double limit = cv[best] + se[best];
            best = Array.FindIndex(cv, v => v <= limit);
        }
        return (best, cv, se);
    }

    private static (double[] Coeffs, double[,] Chol) Solve(double[] theta, double[] e, double[] w, int order, double lambda)
    {
        int p = 2 * order + 1;
        var a = new double[p, p];
        var b = new double[p];
        var row = new double[p];
        for (int i = 0; i < theta.Length; i++)
        {
            FourierSeries.DesignRow(theta[i], order, row);
            for (int r = 0; r < p; r++)
            {
                double wr = w[i] * row[r];
                b[r] += wr * e[i];
                for (int c = 0; c <= r; c++) a[r, c] += wr * row[c];
            }
        }
        for (int r = 0; r < p; r++) for (int c = r + 1; c < p; c++) a[r, c] = a[c, r];
        for (int k = 1; k <= order; k++)
        {
            double pen = lambda * Math.Pow(k, 4);
            a[2 * k - 1, 2 * k - 1] += pen;
            a[2 * k, 2 * k] += pen;
        }
        var l = LinearAlgebra.Cholesky(a);
        return (LinearAlgebra.Solve(l, b), l);
    }

    private static double[] Gains(double[] c, double[] theta, GainBounds bounds) =>
        theta.Select(t => GainModel.GainFromBias(FourierSeries.Evaluate(c, t), bounds)).ToArray();

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double[] Concat(double[] a, double[] b) => [.. a, .. b];
    private static double[] Pick(double[] a, int[] idx) => idx.Select(i => a[i]).ToArray();
}
