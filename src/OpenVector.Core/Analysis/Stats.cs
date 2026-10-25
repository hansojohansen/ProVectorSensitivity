namespace OpenVector.Core.Analysis;

internal static class Stats
{
    public static double Mean(IReadOnlyList<double> v)
    {
        double s = 0;
        for (int i = 0; i < v.Count; i++) s += v[i];
        return s / v.Count;
    }

    /// <summary>Sample standard deviation (ddof = 1).</summary>
    public static double StdDev(IReadOnlyList<double> v)
    {
        double m = Mean(v), s = 0;
        for (int i = 0; i < v.Count; i++) s += (v[i] - m) * (v[i] - m);
        return Math.Sqrt(s / (v.Count - 1));
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }

    /// <summary>numpy.percentile(method="linear") on a sorted array, using numpy's lerp form.</summary>
    public static double PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        int n = sorted.Count;
        double h = p / 100.0 * (n - 1);
        int lo = (int)Math.Floor(h);
        int hi = Math.Min(lo + 1, n - 1);
        double t = h - lo, a = sorted[lo], b = sorted[hi], d = b - a;
        return t >= 0.5 ? b - d * (1 - t) : a + d * t;
    }
}
