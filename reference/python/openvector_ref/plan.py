"""Balanced trial-plan generator (mirrors OpenVector.Core.Analysis.TrialPlanGenerator).

Each (sector, amplitude) cell gets `reps` trials; each trial's direction is drawn uniformly
(continuous) inside its sector (sectors centred on 2*pi*s/S). The list is then shuffled with
Fisher-Yates. All random draws come from SplitMix64(seed) in a fixed order:
  1. for s in sectors, for a in amplitudes, for r in reps: one draw for the angle;
  2. Fisher-Yates from the end: j = next_int(i + 1).
"""
from .geometry import TWO_PI, normalize_angle
from .rng import SplitMix64


def generate_plan(seed: int, sectors: int = 32, amplitudes=(1.0, 2.0, 3.0), reps: int = 7):
    rng = SplitMix64(seed)
    trials = []
    for s in range(sectors):
        for a, amp in enumerate(amplitudes):
            for _ in range(reps):
                u = rng.next_double()
                theta = normalize_angle((s - 0.5 + u) * TWO_PI / sectors)
                trials.append({"sector": s, "amplitude_index": a, "amplitude": float(amp), "theta": theta})
    for i in range(len(trials) - 1, 0, -1):
        j = rng.next_int(i + 1)
        trials[i], trials[j] = trials[j], trials[i]
    for i, t in enumerate(trials):
        t["index"] = i
    return trials
