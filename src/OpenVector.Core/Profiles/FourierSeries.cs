namespace OpenVector.Core.Profiles;

/// <summary>Real Fourier series with coefficient layout [c0, a1, b1, a2, b2, ..., aK, bK].</summary>
public static class FourierSeries
{
    public static int OrderOf(int coeffCount) => (coeffCount - 1) / 2;

    public static double Evaluate(ReadOnlySpan<double> c, double theta)
    {
        // Same summation order as the Python reference (design-row dot product).
        double f = c[0];
        for (int k = 1; k <= OrderOf(c.Length); k++)
            f += c[2 * k - 1] * Math.Cos(k * theta) + c[2 * k] * Math.Sin(k * theta);
        return f;
    }

    /// <summary>Fill the design row x(theta) of length 2K+1.</summary>
    public static void DesignRow(double theta, int order, Span<double> row)
    {
        row[0] = 1.0;
        for (int k = 1; k <= order; k++)
        {
            row[2 * k - 1] = Math.Cos(k * theta);
            row[2 * k] = Math.Sin(k * theta);
        }
    }
}
