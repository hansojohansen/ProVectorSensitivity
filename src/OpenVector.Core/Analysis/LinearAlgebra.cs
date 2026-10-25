namespace OpenVector.Core.Analysis;

/// <summary>Small dense SPD solver (Cholesky). The normal matrices here are at most 17×17.</summary>
internal static class LinearAlgebra
{
    /// <summary>Cholesky factor L (lower) of SPD matrix A (row-major n×n), in place copy.</summary>
    public static double[,] Cholesky(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (s <= 0) throw new InvalidOperationException("Normal matrix is not positive definite.");
                    l[i, i] = Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        }
        return l;
    }

    public static double[] Solve(double[,] l, double[] b)
    {
        int n = b.Length;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int k = 0; k < i; k++) s -= l[i, k] * y[k];
            y[i] = s / l[i, i];
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int k = i + 1; k < n; k++) s -= l[k, i] * x[k];
            x[i] = s / l[i, i];
        }
        return x;
    }

    public static double[][] Inverse(double[,] l)
    {
        int n = l.GetLength(0);
        var inv = new double[n][];
        for (int j = 0; j < n; j++) inv[j] = new double[n];
        var e = new double[n];
        for (int j = 0; j < n; j++)
        {
            Array.Clear(e);
            e[j] = 1;
            var col = Solve(l, e);
            for (int i = 0; i < n; i++) inv[i][j] = col[i];
        }
        return inv;
    }
}
