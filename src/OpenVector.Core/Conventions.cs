namespace OpenVector.Core;

/// <summary>
/// Shared angle conventions (DECISIONS D14): theta = atan2(dy, dx) in raw-count coordinates
/// (x right, y DOWN), radians in [0, 2pi); 0 = right, pi/2 = down, pi = left, 3pi/2 = up.
/// Sectors/bins of n are centred on 2*pi*k/n, so sector 0 is centred on "right".
/// </summary>
public static class Angles
{
    public const double TwoPi = 2.0 * Math.PI;

    /// <summary>Map any angle to [0, 2pi).</summary>
    public static double Normalize(double theta)
    {
        double t = theta % TwoPi; // fmod, same as the Python reference (keeps [0, 2pi) inputs bit-exact)
        if (t < 0) t += TwoPi;
        return t >= TwoPi ? 0.0 : t;
    }

    /// <summary>Direction of a count-space displacement, in [0, 2pi).</summary>
    public static double Direction(double dx, double dy) => Normalize(Math.Atan2(dy, dx));

    /// <summary>Signed difference a - b wrapped to [-pi, pi].</summary>
    public static double Delta(double a, double b) => Math.IEEERemainder(a - b, TwoPi);

    public static int SectorOf(double theta, int n) =>
        (int)(((long)Math.Floor(Normalize(theta) / TwoPi * n + 0.5)) % n);

    public static double SectorCenter(int k, int n) => TwoPi * k / n;

    /// <summary>
    /// Relative along/across error of <paramref name="p"/> for the movement start→target:
    /// along = (P−S)·u/A − 1 (&gt;0 overshoot), across = (P−S)·n/A with n = u rotated by +pi/2 in theta.
    /// </summary>
    public static (double Along, double Across) AlongAcross(
        double sx, double sy, double tx, double ty, double px, double py)
    {
        double vx = tx - sx, vy = ty - sy;
        double a = Math.Sqrt(vx * vx + vy * vy);
        double ux = vx / a, uy = vy / a;
        double qx = px - sx, qy = py - sy;
        return ((qx * ux + qy * uy) / a - 1.0, (-qx * uy + qy * ux) / a);
    }
}
