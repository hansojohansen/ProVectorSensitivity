namespace OpenVector.RawAccel.Model;

/// <summary>
/// Exact C# port of Raw Accel 1.7.1 <c>modifier::modify</c> (common/rawaccel.hpp, commit 53a7213) for the
/// stateless subset OpenVector uses: rotation, angle snapping, speed clamp, whole mode with directional
/// range weights, by-component mode, noaccel and LUT curves, output-DPI normalisation, Y/X ratio and the
/// left/up multipliers. EMA smoothing and the analytic curves (classic, natural, ...) are not ported.
/// Coordinates are raw counts: +x right, +y down (D14).
/// </summary>
public sealed class RawAccelModel
{
    /// <summary>common/rawaccel-base.hpp NORMALIZED_DPI.</summary>
    public const double NormalizedDpi = 1000;
    /// <summary>common/rawaccel-base.hpp MAX_NORM.</summary>
    public const double MaxNorm = 16;
    /// <summary>common/rawaccel-base.hpp LUT_RAW_DATA_CAPACITY.</summary>
    public const int LutRawDataCapacity = 514;
    public const int LutPointsCapacity = LutRawDataCapacity / 2;

    private readonly RawAccelProfile _p;
    private readonly bool _applyRotate, _computeRefAngle, _applySnap, _clampSpeed, _applyDirectionalWeight, _applyDirMulX, _applyDirMulY;
    private readonly double _rotX, _rotY;
    private readonly double _outputDpiAdjustmentFactor;
    private enum DistMode { Euclidean, Separate, Max, Lp }
    private readonly DistMode _dist;

    public RawAccelModel(RawAccelProfile profile)
    {
        _p = profile ?? throw new ArgumentNullException(nameof(profile));
        // modifier_flags(const profile&)
        _clampSpeed = _p.SpeedMax > 0 && _p.SpeedMin <= _p.SpeedMax;
        _applyRotate = _p.DegreesRotation != 0;
        _applySnap = _p.DegreesSnap != 0;
        _applyDirectionalWeight = _p.Whole && _p.RangeWeightX != _p.RangeWeightY;
        _computeRefAngle = _applySnap || _applyDirectionalWeight;
        _applyDirMulX = _p.LrOutputDpiRatio != 1;
        _applyDirMulY = _p.UdOutputDpiRatio != 1;
        // direction(degrees) in math-vec2.hpp: { cos(rad), sin(rad) }
        double rad = _p.DegreesRotation * Math.PI / 180;
        _rotX = Math.Cos(rad); _rotY = Math.Sin(rad);
        _outputDpiAdjustmentFactor = _p.OutputDpi / NormalizedDpi;
        // speed_processor::init
        if (!_p.Whole) _dist = DistMode.Separate;
        else if (_p.LpNorm >= MaxNorm || _p.LpNorm <= 0) _dist = DistMode.Max;
        else if (_p.LpNorm != 2) _dist = DistMode.Lp;
        else _dist = DistMode.Euclidean;
        CheckSupported(_p.AccelX);
        if (!_p.Whole) CheckSupported(_p.AccelY);
    }

    public RawAccelProfile Profile => _p;

    private static void CheckSupported(AccelCurve c)
    {
        if (c.Mode is not (AccelMode.NoAccel or AccelMode.Lut))
            throw new NotSupportedException($"Accel mode '{c.Mode}' is not ported to RawAccelModel (only noaccel and lut).");
    }

    /// <summary>
    /// Driver's input normalisation (driver.cpp DeviceSetup): 1000/dpi if the device DPI is set, else 1.
    /// </summary>
    public static double DpiFactorForDeviceDpi(int deviceDpi) => deviceDpi > 0 ? NormalizedDpi / deviceDpi : 1;

    /// <summary>Applies one packet. <paramref name="timeMs"/> only matters for speed-dependent curves and the speed cap.</summary>
    public (double X, double Y) Modify(double x, double y, double dpiFactor = 1, double timeMs = 1)
    {
        double referenceAngle = 0;
        double ipsFactor = dpiFactor / timeMs;

        if (_applyRotate)
        {
            // rotate(v, dir) in math-vec2.hpp: { v.x*d.x - v.y*d.y, v.x*d.y + v.y*d.x }
            double rx = x * _rotX - y * _rotY;
            double ry = x * _rotY + y * _rotX;
            x = rx; y = ry;
        }

        if (_computeRefAngle && y != 0)
        {
            if (x == 0)
            {
                referenceAngle = Math.PI / 2;
            }
            else
            {
                referenceAngle = Math.Atan(Math.Abs(y / x));
                if (_applySnap)
                {
                    double snap = _p.DegreesSnap * Math.PI / 180;
                    if (referenceAngle > Math.PI / 2 - snap)
                    {
                        referenceAngle = Math.PI / 2;
                        double m = Magnitude(x, y);
                        x = 0; y = Math.CopySign(m, y);
                    }
                    else if (referenceAngle < snap)
                    {
                        referenceAngle = 0;
                        double m = Magnitude(x, y);
                        x = Math.CopySign(m, x); y = 0;
                    }
                }
            }
        }

        if (_clampSpeed)
        {
            double speed = Magnitude(x, y) * ipsFactor;
            double ratio = Math.Clamp(speed, _p.SpeedMin, _p.SpeedMax) / speed;
            x *= ratio; y *= ratio;
        }

        double awx = Math.Abs(x * ipsFactor * _p.DomainWeightX);
        double awy = Math.Abs(y * ipsFactor * _p.DomainWeightY);

        if (_dist == DistMode.Separate)
        {
            double scaleX = Callback(_p.AccelX, awx, _p.RangeWeightX);
            double scaleY = Callback(_p.AccelY, awy, _p.RangeWeightY);
            x *= scaleX; y *= scaleY;
        }
        else
        {
            double speed = _dist switch
            {
                DistMode.Max => Math.Max(awx, awy),
                DistMode.Lp => LpDistance(awx, awy, _p.LpNorm),
                _ => Magnitude(awx, awy),
            };
            double weight = _p.RangeWeightX;
            if (_applyDirectionalWeight)
            {
                double diff = _p.RangeWeightY - _p.RangeWeightX;
                weight += 2 / Math.PI * referenceAngle * diff;
            }
            double scale = Callback(_p.AccelX, speed, weight);
            x *= scale; y *= scale;
        }

        double dpiAdjustment = _outputDpiAdjustmentFactor * dpiFactor;
        x *= dpiAdjustment;
        y *= dpiAdjustment * _p.YxOutputDpiRatio;

        if (_applyDirMulX && x < 0) x *= _p.LrOutputDpiRatio;
        if (_applyDirMulY && y < 0) y *= _p.UdOutputDpiRatio;
        return (x, y);
    }

    /// <summary>
    /// Gain |out|/|in| and output direction ψ for a unit input in direction θ (radians, D14 convention).
    /// Normalised by the output-DPI factor of the identity profile (output DPI 1000, device DPI unset), so the
    /// stock default profile gives g ≡ 1.
    /// </summary>
    public (double Gain, double OutputAngle) Evaluate(double theta, double inputMagnitude = 1, double dpiFactor = 1, double timeMs = 1)
    {
        double vx = Math.Cos(theta) * inputMagnitude, vy = Math.Sin(theta) * inputMagnitude;
        var (ox, oy) = Modify(vx, vy, dpiFactor, timeMs);
        double g = Magnitude(ox, oy) / Magnitude(vx, vy);
        double psi = Math.Atan2(oy, ox);
        if (psi < 0) psi += 2 * Math.PI;
        return (g, psi);
    }

    /// <summary>Signed direction error ψ − θ wrapped to (−π, π].</summary>
    public static double WrapAngle(double a)
    {
        a = Math.IEEERemainder(a, 2 * Math.PI);
        return a <= -Math.PI ? a + 2 * Math.PI : a;
    }

    private static double Callback(AccelCurve c, double x, double rangeWeight) =>
        1 + (Accel(c, x) - 1) * rangeWeight;

    private static double Accel(AccelCurve c, double x) => c.Mode switch
    {
        AccelMode.NoAccel => 1,
        AccelMode.Lut => Lookup(c, x),
        _ => throw new NotSupportedException(c.Mode.ToString()),
    };

    /// <summary>Port of accel-lookup.hpp <c>lookup::operator()</c>.</summary>
    public static double Lookup(AccelCurve c, double x)
    {
        var d = c.Data;
        int size = d.Count / 2;
        bool velocity = c.Gain;
        int lo = 0, hi = size - 2;
        if (x <= 0) return 0;
        if (hi < LutPointsCapacity - 1)
        {
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                double px = d[2 * mid], py = d[2 * mid + 1];
                if (x < px) hi = mid - 1;
                else if (x > px) lo = mid + 1;
                else
                {
                    double y = py;
                    if (velocity) y /= x;
                    return y;
                }
            }
            if (lo > 0)
            {
                double ax = d[2 * (lo - 1)], ay = d[2 * (lo - 1) + 1];
                double bx = lo < size ? d[2 * lo] : 0, by = lo < size ? d[2 * lo + 1] : 0; // driver reads zero-filled tail
                double t = (x - ax) / (bx - ax);
                double y = Lerp(ay, by, t);
                if (velocity) y /= x;
                return y;
            }
        }
        double y0 = d[1];
        if (velocity) y0 /= d[0];
        return y0;
    }

    private static double Lerp(double a, double b, double t)
    {
        double x = a + t * (b - a);
        if ((t > 1) == (a < b)) return Math.Max(x, b);
        return Math.Min(x, b);
    }

    private static double Magnitude(double x, double y) => Math.Sqrt(x * x + y * y);

    // math-vec2.hpp lp_distance: pow(pow(|x|,p) + pow(|y|,p), 1/p)
    private static double LpDistance(double x, double y, double p) =>
        Math.Pow(Math.Pow(Math.Abs(x), p) + Math.Pow(Math.Abs(y), p), 1 / p);
}
