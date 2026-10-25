namespace OpenVector.RawAccel.Projection;

/// <summary>
/// Small bound-constrained Levenberg–Marquardt solver (projected steps, numerical central-difference Jacobian).
/// Adequate for the handful of parameters the projection uses; not a general-purpose optimiser.
/// </summary>
internal static class LevenbergMarquardt
{
    public sealed record Result(double[] X, double Cost, int Iterations, bool Converged);

    /// <param name="residuals">Fills r (length m) for parameters x.</param>
    public static Result Minimize(Action<double[], double[]> residuals, int m, double[] x0, double[] lower, double[] upper,
        int maxIterations = 200, double tol = 1e-14)
    {
        int n = x0.Length;
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = Math.Clamp(x0[i], lower[i], upper[i]);
        var r = new double[m];
        residuals(x, r);
        double cost = Dot(r, r);
        var J = new double[m, n];
        var rp = new double[m]; var rm = new double[m];
        var xt = new double[n];
        double mu = 1e-3;
        bool converged = false;
        int it = 0;
        for (; it < maxIterations; it++)
        {
            // Jacobian (central differences, one-sided at bounds)
            for (int j = 0; j < n; j++)
            {
                double h = 1e-7 * Math.Max(1, Math.Abs(x[j]));
                Array.Copy(x, xt, n);
                double xp = Math.Min(x[j] + h, upper[j]), xm = Math.Max(x[j] - h, lower[j]);
                xt[j] = xp; residuals(xt, rp);
                xt[j] = xm; residuals(xt, rm);
                double d = xp - xm;
                for (int i = 0; i < m; i++) J[i, j] = d > 0 ? (rp[i] - rm[i]) / d : 0;
            }
            var A = new double[n, n]; var g = new double[n];
            for (int a = 0; a < n; a++)
            {
                double s = 0;
                for (int i = 0; i < m; i++) s += J[i, a] * r[i];
                g[a] = s;
                for (int b = a; b < n; b++)
                {
                    double t = 0;
                    for (int i = 0; i < m; i++) t += J[i, a] * J[i, b];
                    A[a, b] = t; A[b, a] = t;
                }
            }
            bool improved = false;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var M = (double[,])A.Clone();
                for (int a = 0; a < n; a++) M[a, a] += mu * Math.Max(A[a, a], 1e-12);
                var step = Solve(M, g.Select(v => -v).ToArray());
                if (step is null) { mu *= 10; continue; }
                for (int a = 0; a < n; a++) xt[a] = Math.Clamp(x[a] + step[a], lower[a], upper[a]);
                residuals(xt, rp);
                double newCost = Dot(rp, rp);
                if (newCost < cost)
                {
                    double rel = (cost - newCost) / Math.Max(cost, 1e-300);
                    double stepNorm = 0;
                    for (int a = 0; a < n; a++) stepNorm = Math.Max(stepNorm, Math.Abs(xt[a] - x[a]));
                    Array.Copy(xt, x, n); Array.Copy(rp, r, m);
                    cost = newCost; mu = Math.Max(mu / 3, 1e-12); improved = true;
                    if (rel < tol || stepNorm < 1e-12) converged = true;
                    break;
                }
                mu *= 4;
                if (mu > 1e12) break;
            }
            if (!improved) { converged = true; break; }
            if (converged) break;
        }
        return new Result(x, cost, it + 1, converged);
    }

    private static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>Gaussian elimination with partial pivoting; null if singular.</summary>
    private static double[]? Solve(double[,] A, double[] b)
    {
        int n = b.Length;
        var M = (double[,])A.Clone(); var v = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int i = c + 1; i < n; i++) if (Math.Abs(M[i, c]) > Math.Abs(M[p, c])) p = i;
            if (Math.Abs(M[p, c]) < 1e-300) return null;
            if (p != c)
            {
                for (int k = 0; k < n; k++) (M[c, k], M[p, k]) = (M[p, k], M[c, k]);
                (v[c], v[p]) = (v[p], v[c]);
            }
            for (int i = c + 1; i < n; i++)
            {
                double f = M[i, c] / M[c, c];
                for (int k = c; k < n; k++) M[i, k] -= f * M[c, k];
                v[i] -= f * v[c];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = v[i];
            for (int k = i + 1; k < n; k++) s -= M[i, k] * x[k];
            x[i] = s / M[i, i];
        }
        return x;
    }
}
