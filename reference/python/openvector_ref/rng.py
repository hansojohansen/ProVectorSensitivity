"""SplitMix64: tiny deterministic PRNG shared bit-for-bit with OpenVector.Core.Analysis.SplitMix64.

Used for the trial plan and the bootstrap so C# and Python produce identical results for a seed.
(The synthetic-user generator uses numpy's RNG; it only needs to be reproducible in Python.)
"""

MASK = (1 << 64) - 1


class SplitMix64:
    def __init__(self, seed: int):
        self.state = seed & MASK

    def next_u64(self) -> int:
        self.state = (self.state + 0x9E3779B97F4A7C15) & MASK
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)

    def next_double(self) -> float:
        """Uniform in [0, 1) with 53 random bits."""
        return (self.next_u64() >> 11) * (1.0 / (1 << 53))

    def next_int(self, n: int) -> int:
        """Uniform integer in [0, n)."""
        return int(self.next_double() * n)
