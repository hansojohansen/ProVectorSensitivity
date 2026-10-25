namespace OpenVector.RawAccel.Projection;

/// <summary>
/// Projection backend "rawaccel-1.7": fits the 5-DOF family reachable by stock Raw Accel 1.7.x with rotation
/// fixed at the user's own value (A §2.1, §2.4, D2). Minimises
/// Σ wᵢ (log g(θᵢ; q) − log g*(θᵢ))² + λ² Σ wᵢ δ(θᵢ; q)² over q = log[G0, κ, ρ, L, U] with box bounds.
/// </summary>
public sealed class RawAccel17Projection : IProjectionBackend
{
    public const string BackendId = "rawaccel-1.7";
    public string Id => BackendId;

    public ProjectionResult Project(ProjectionTarget target, ProjectionOptions? options = null, ProjectionBaseline? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        var opt = options ?? ProjectionOptions.Default;
        var bl = baseline ?? ProjectionBaseline.Identity;
        if (!(bl.YxRatio > 0) || !(bl.LrRatio > 0) || !(bl.UdRatio > 0))
            throw new ArgumentException("Baseline ratios must be > 0 (a negative Y/X ratio inverts y and is not supported).");
        if (opt.MinRatio <= 0 || opt.MinRatio > 1 || opt.MaxRatio < 1 || opt.MinKappa <= 0 || opt.MinKappa > 1 || opt.MaxKappa < 1 || opt.MinG0 <= 0 || opt.MaxG0 < opt.MinG0)
            throw new ArgumentException("Invalid projection bounds.");

        var warnings = new List<string>();
        bool axisOnly = opt.Mode switch
        {
            ProjectionMode.AxisOnly => true,
            ProjectionMode.Full when !bl.ShapeAvailable => throw new InvalidOperationException(
                "Full projection requested but κ is unavailable: " + (bl.ShapeUnavailableReason ?? "baseline has an accel curve")),
            ProjectionMode.Full => false,
            _ => !bl.ShapeAvailable,
        };
        if (axisOnly && !bl.ShapeAvailable)
            warnings.Add("Your Raw Accel profile already has an acceleration curve, so only the base gain, Y/X ratio and left/up multipliers are used (no diagonal shape). " + (bl.ShapeUnavailableReason ?? ""));
        if (bl.SnapDeg != 0)
            warnings.Add($"Angle snapping ({bl.SnapDeg}°) is active in your profile; the fit ignores it near the axes.");

        int n = target.Theta.Count;
        var th = target.Theta.ToArray();
        var logT = target.Gain.Select(g => Math.Log(g)).ToArray();
        var w = NormalisedWeights(target.Weight, n);
        var sw = w.Select(Math.Sqrt).ToArray();
        double lam = opt.DirectionPenalty;
        int m = lam != 0 ? 2 * n : n;

        // initial guess from the four axes (A §2.4 step 3)
        double g0 = InterpPeriodic(th, target.Gain, 0), g90 = InterpPeriodic(th, target.Gain, Math.PI / 2),
               g180 = InterpPeriodic(th, target.Gain, Math.PI), g270 = InterpPeriodic(th, target.Gain, 3 * Math.PI / 2);
        double R = g90 / g0;
        double[] full0 = axisOnly
            ? new[] { g0, 1, R, g180 / g0, g270 / g90 }
            : new[] { g0, Math.Sqrt(R), Math.Sqrt(R), g180 / g0, g270 / g90 };
        double[] lo = { opt.MinG0, opt.MinKappa, opt.MinRatio, opt.MinRatio, opt.MinRatio };
        double[] hi = { opt.MaxG0, opt.MaxKappa, opt.MaxRatio, opt.MaxRatio, opt.MaxRatio };
        int[] free = axisOnly ? new[] { 0, 2, 3, 4 } : new[] { 0, 1, 2, 3, 4 };

        double[] Expand(double[] q)
        {
            var p = new double[] { 1, 1, 1, 1, 1 };
            for (int j = 0; j < free.Length; j++) p[free[j]] = Math.Exp(q[j]);
            return p;
        }

        void Residuals(double[] q, double[] r)
        {
            var p = Expand(q);
            for (int i = 0; i < n; i++)
            {
                var (g, d) = Evaluate(th[i], p[0], p[1], p[2], p[3], p[4], bl);
                r[i] = sw[i] * (Math.Log(g) - logT[i]);
                if (lam != 0) r[n + i] = lam * sw[i] * d;
            }
        }

        var x0 = free.Select(j => Math.Log(Math.Clamp(full0[j], lo[j], hi[j]))).ToArray();
        var lb = free.Select(j => Math.Log(lo[j])).ToArray();
        var ub = free.Select(j => Math.Log(hi[j])).ToArray();
        var sol = LevenbergMarquardt.Minimize(Residuals, m, x0, lb, ub, opt.MaxIterations);
        var pf = Expand(sol.X);
        var prm = new DirectionalParams(pf[0], pf[1], pf[2], pf[3], pf[4]);
        for (int j = 0; j < 5; j++)
            if (pf[j] <= lo[j] * (1 + 1e-9) || pf[j] >= hi[j] * (1 - 1e-9))
                if (!(axisOnly && j == 1)) warnings.Add($"Parameter {Names[j]} hit its bound ({pf[j]:G4}).");

        return BuildResult(target, th, w, prm, bl, opt, axisOnly, sol.Iterations, sol.Converged, warnings);
    }

    private static readonly string[] Names = { "G0", "kappa", "rho", "L", "U" };

    /// <summary>Evaluates a given parameter set against a target with this backend's metrics.</summary>
    public static ProjectionResult Evaluate(ProjectionTarget target, DirectionalParams p, ProjectionBaseline? baseline = null, ProjectionOptions? options = null)
    {
        target.Validate();
        var th = target.Theta.ToArray();
        return BuildResult(target, th, NormalisedWeights(target.Weight, th.Length), p, baseline ?? ProjectionBaseline.Identity,
            options ?? ProjectionOptions.Default, p.Kappa == 1, 0, true, new List<string>());
    }

    private static ProjectionResult BuildResult(ProjectionTarget target, double[] th, double[] w, DirectionalParams p, ProjectionBaseline bl,
        ProjectionOptions opt, bool axisOnly, int iterations, bool converged, List<string> warnings)
    {
        int n = th.Length;
        var applied = new double[n]; var rel = new double[n]; var tilt = new double[n];
        double sw = 0, ssRel = 0, maxRel = 0, maxTilt = 0, meanLogT = 0;
        for (int i = 0; i < n; i++)
        {
            var (g, d) = Evaluate(th[i], p.G0, p.Kappa, p.Rho, p.L, p.U, bl);
            applied[i] = g; rel[i] = g / target.Gain[i] - 1; tilt[i] = d * 180 / Math.PI;
            sw += w[i]; ssRel += w[i] * rel[i] * rel[i];
            meanLogT += w[i] * Math.Log(target.Gain[i]);
            if (w[i] > 0) { maxRel = Math.Max(maxRel, Math.Abs(rel[i])); maxTilt = Math.Max(maxTilt, Math.Abs(tilt[i])); }
        }
        meanLogT /= sw;
        double cst = Math.Exp(meanLogT), ssBias = 0, ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double lt = Math.Log(target.Gain[i]);
            double b = cst / target.Gain[i] - 1;
            ssBias += w[i] * b * b;
            ssTot += w[i] * (lt - meanLogT) * (lt - meanLogT);
            double lr = Math.Log(applied[i]) - lt;
            ssRes += w[i] * lr * lr;
        }
        double rms = Math.Sqrt(ssRel / sw), biasRms = Math.Sqrt(ssBias / sw);
        const double eps = 1e-12;
        double ev = ssTot < eps ? 1 : 1 - ssRes / ssTot;
        double frac = biasRms < 1e-9 ? 1 : Math.Clamp(1 - rms / biasRms, 0, 1);
        return new ProjectionResult
        {
            BackendId = BackendId,
            Params = p,
            Baseline = bl,
            Options = opt,
            AxisOnly = axisOnly,
            Residual = new ProjectionResidualCurve(th, target.Gain.ToArray(), applied, rel, tilt),
            RmsResidual = rms,
            MaxAbsResidual = maxRel,
            BiasRms = biasRms,
            ExplainedVariance = ev,
            FractionOfBiasApplied = frac,
            MaxTiltDeg = maxTilt,
            Iterations = iterations,
            Converged = converged,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Closed form (A §2.1) of gain relative to the baseline and the added output-direction change δ, for input
    /// direction θ. With an identity baseline this equals fit_rawaccel.py <c>model()</c>.
    /// </summary>
    public static (double Gain, double Tilt) Evaluate(double theta, double g0, double kappa, double rho, double l, double u, ProjectionBaseline bl)
    {
        double t = theta + bl.RotationDeg * Math.PI / 180;
        double c = Math.Cos(t), s = Math.Sin(t);
        double phi = Math.Atan2(Math.Abs(s), Math.Abs(c));
        double k = 1 + (kappa - 1) * phi / (Math.PI / 2);
        double lb = c < 0 ? bl.LrRatio : 1, lt = c < 0 ? bl.LrRatio * l : 1;
        double ub = s < 0 ? bl.UdRatio : 1, ut = s < 0 ? bl.UdRatio * u : 1;
        double oxT = g0 * k * lt * c, oyT = g0 * k * bl.YxRatio * rho * ut * s;
        double oxB = lb * c, oyB = bl.YxRatio * ub * s;
        double g = Math.Sqrt(oxT * oxT + oyT * oyT) / Math.Sqrt(oxB * oxB + oyB * oyB);
        double d = Model.RawAccelModel.WrapAngle(Math.Atan2(oyT, oxT) - Math.Atan2(oyB, oxB));
        return (g, d);
    }

    private static double[] NormalisedWeights(IReadOnlyList<double>? weight, int n)
    {
        if (weight is null) return Enumerable.Repeat(1.0, n).ToArray();
        double mean = weight.Average();
        return weight.Select(v => v / mean).ToArray();
    }

    /// <summary>Periodic linear interpolation of samples at angle a.</summary>
    internal static double InterpPeriodic(double[] theta, IReadOnlyList<double> values, double a)
    {
        const double TwoPi = 2 * Math.PI;
        static double Norm(double x) => ((x % TwoPi) + TwoPi) % TwoPi;
        int n = theta.Length;
        if (n == 1) return values[0];
        var pts = Enumerable.Range(0, n).Select(i => (t: Norm(theta[i]), v: values[i])).OrderBy(p => p.t).ToArray();
        a = Norm(a);
        // segment i goes from pts[i] to pts[i+1] (the last one wraps to pts[0] + 2π)
        for (int i = 0; i < n; i++)
        {
            double t0 = pts[i].t, t1 = i + 1 < n ? pts[i + 1].t : pts[0].t + TwoPi;
            double v1 = pts[(i + 1) % n].v;
            foreach (double aa in new[] { a, a + TwoPi })
                if (aa >= t0 && aa <= t1)
                    return t1 == t0 ? pts[i].v : pts[i].v + (v1 - pts[i].v) * (aa - t0) / (t1 - t0);
        }
        return pts[0].v;
    }
}
