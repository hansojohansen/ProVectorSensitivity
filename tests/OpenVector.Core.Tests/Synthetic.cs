using OpenVector.Core.Analysis;
using OpenVector.Core.Data;
using OpenVector.Core.Profiles;

namespace OpenVector.Core.Tests;

/// <summary>
/// Endpoint-level synthetic users (the full packet-level motor model lives in reference/python/openvector_ref/synth.py).
/// Primary endpoint = S + A[(1 + b(θ) + e_a) u + e_c n]; final endpoint lands near the target.
/// </summary>
internal static class Synthetic
{
    public static readonly double[] Identity = [0.0];
    /// <summary>−8 % extent (undershoot) flicking up (θ = 3π/2), 0 flicking down.</summary>
    public static readonly double[] UpUndershoot = [-0.03, 0.0, 0.04, 0.01, 0.0];
    /// <summary>sin 2θ: the "/" vs "\" diagonal asymmetry Raw Accel 1.7 cannot express.</summary>
    public static readonly double[] Diagonal = [0.0, 0.0, 0.0, 0.0, 0.05];
    /// <summary>+4 % overshoot to the right, −4 % to the left.</summary>
    public static readonly double[] LeftRight = [0.0, 0.04, 0.0];
    public static readonly double[] Rich = [-0.01, 0.03, -0.02, 0.015, 0.04, -0.01, 0.01, 0.0, 0.012];

    public static double TrueGain(double[] bias, double theta) => 1.0 / (1.0 + FourierSeries.Evaluate(bias, theta));

    private static readonly double[] Amps = [230.0, 680.0, 1360.0];

    public static List<MovementTrial> Session(double[] bias, int seed, int reps = 7, Func<PlannedTrial, bool>? filter = null,
        double sigmaAlong = 0.10, double sigmaAcross = 0.05, double reversalBias = 0.0, double reactionUpExtraUs = 0.0)
    {
        var plan = TrialPlanGenerator.Generate(new TrialPlanOptions { Seed = (ulong)seed, RepsPerCell = reps, Amplitudes = Amps })
            .Where(p => filter?.Invoke(p) ?? true).ToList();
        var rng = new Random(seed * 7919 + 1);
        double Normal() => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(Angles.TwoPi * rng.NextDouble());
        var list = new List<MovementTrial>();
        long t = 1_000_000;
        PreviousTrial? prev = null;
        foreach (var p in plan)
        {
            double th = p.Theta, a = p.Amplitude;
            double ux = Math.Cos(th), uy = Math.Sin(th);
            double m = 1.0 + FourierSeries.Evaluate(bias, th) + sigmaAlong * Normal();
            if (prev is not null && Math.Abs(Angles.Delta(th, prev.DirectionTheta)) > 0.75 * Math.PI) m += reversalBias;
            double c = sigmaAcross * Normal();
            double px = a * (m * ux - c * uy), py = a * (m * uy + c * ux);
            double fx = a * ux + 0.02 * a * Normal(), fy = a * uy + 0.02 * a * Normal();
            long onScreen = t + 4000;
            long rt = (long)(200_000 + 30_000 * Normal() + reactionUpExtraUs * Math.Max(0, -uy));
            long onset = onScreen + rt;
            long click = onset + 350_000 + (long)(40_000 * Normal());
            list.Add(new MovementTrial
            {
                TrialId = $"syn-{seed}-{p.Index}",
                SessionId = $"syn-{seed}",
                Source = new TrialSource { Kind = SourceKind.Synthetic },
                Provenance = new Provenance { TargetTruth = TargetTruth.Measured, Confidence = 1.0 },
                Mapping = new TrialMapping { YawDegPerCount = 0.022, PitchDegPerCount = 0.022 },
                Condition = new TrialCondition { TrialIndex = p.Index, Sector = p.Sector, SectorCount = 32, AmplitudeIndex = p.AmplitudeIndex },
                Context = new TrialContext { TrialIndexSession = p.Index, Previous = prev },
                Geometry = new TrialGeometry
                {
                    Start = CountPoint.At(0, 0),
                    Target = new TargetPoint { XCounts = a * ux, YCounts = a * uy, RadiusCounts = 40 },
                    AmplitudeCounts = a,
                    DirectionTheta = th,
                },
                Outcome = new TrialOutcome { EndpointPrimary = CountPoint.At(px, py), EndpointFinal = CountPoint.At(fx, fy) },
                Timing = new TrialTiming
                {
                    TStimulusUs = t, TStimulusOnScreenUs = onScreen, TOnsetUs = onset, TFinalClickUs = click,
                    ReactionTimeUs = rt, ReactionTimeBasis = ReactionTimeBasis.OnScreen,
                },
                Kinematics = new TrialKinematics { PeakSpeedCountsPerS = 1.875 * a / 0.25, PathLengthCounts = 1.05 * a },
            });
            prev = new PreviousTrial { DirectionTheta = th, AmplitudeCounts = a };
            t = click + 600_000;
        }
        return list;
    }

    /// <summary>Minimum-jerk submovements (t0 s, duration s, dx, dy) sampled to integer packets at hz; zero packets dropped.</summary>
    public static (long[] T, int[] Dx, int[] Dy) Packets((double T0, double Dur, double Dx, double Dy)[] subs, double tEndS, double hz)
    {
        static double Mj(double tau) { tau = Math.Clamp(tau, 0, 1); return 10 * Math.Pow(tau, 3) - 15 * Math.Pow(tau, 4) + 6 * Math.Pow(tau, 5); }
        var t = new List<long>(); var dx = new List<int>(); var dy = new List<int>();
        long cx = 0, cy = 0;
        for (int k = 1; k * 1e6 / hz <= tEndS * 1e6; k++)
        {
            double ts = k / hz;
            double px = 0, py = 0;
            foreach (var s in subs) { double f = Mj((ts - s.T0) / s.Dur); px += f * s.Dx; py += f * s.Dy; }
            long nx = (long)Math.Round(px), ny = (long)Math.Round(py);
            if (nx != cx || ny != cy) { t.Add((long)Math.Round(k * 1e6 / hz)); dx.Add((int)(nx - cx)); dy.Add((int)(ny - cy)); }
            cx = nx; cy = ny;
        }
        return (t.ToArray(), dx.ToArray(), dy.ToArray());
    }
}
