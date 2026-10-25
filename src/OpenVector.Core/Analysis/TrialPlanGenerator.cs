namespace OpenVector.Core.Analysis;

/// <summary>One planned flick. <see cref="Theta"/> is the count-space direction (D14); the trainer converts it
/// to a view direction using the mapping (yaw/pitch deg per count may differ).</summary>
public sealed record PlannedTrial(int Index, int Sector, int AmplitudeIndex, double Amplitude, double Theta);

public sealed record TrialPlanOptions
{
    public ulong Seed { get; init; }
    public int Sectors { get; init; } = 32;
    /// <summary>Amplitudes in the caller's unit (e.g. degrees of view rotation).</summary>
    public IReadOnlyList<double> Amplitudes { get; init; } = [5.0, 15.0, 30.0];
    /// <summary>Trials per (sector, amplitude) cell. D9 needs ≥20 valid trials per sector: reps ≥ 7 for 3 amplitudes.</summary>
    public int RepsPerCell { get; init; } = 7;
}

/// <summary>
/// Balanced, randomised flick plan (D2 revision): every (sector, amplitude) cell gets RepsPerCell trials, each with
/// a continuous direction drawn uniformly inside its sector (sectors centred on 2πs/S), then Fisher–Yates shuffled.
/// Deterministic for a seed and identical to reference/python/openvector_ref/plan.py.
/// </summary>
public static class TrialPlanGenerator
{
    public static IReadOnlyList<PlannedTrial> Generate(TrialPlanOptions o)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(o.Sectors, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.RepsPerCell, 1);
        var rng = new SplitMix64(o.Seed);
        var list = new List<(int s, int a, double theta)>();
        for (int s = 0; s < o.Sectors; s++)
            for (int a = 0; a < o.Amplitudes.Count; a++)
                for (int r = 0; r < o.RepsPerCell; r++)
                {
                    double u = rng.NextDouble();
                    list.Add((s, a, Angles.Normalize((s - 0.5 + u) * Angles.TwoPi / o.Sectors)));
                }
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list.Select((t, i) => new PlannedTrial(i, t.s, t.a, o.Amplitudes[t.a], t.theta)).ToArray();
    }
}
