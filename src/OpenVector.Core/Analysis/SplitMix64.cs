namespace OpenVector.Core.Analysis;

/// <summary>
/// SplitMix64 PRNG, bit-identical to reference/python/openvector_ref/rng.py. Used for the trial plan and
/// the bootstrap so C# and the Python reference agree exactly for a given seed.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, n).</summary>
    public int NextInt(int n) => (int)(NextDouble() * n);
}
