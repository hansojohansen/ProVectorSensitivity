"""Sequential effects and reaction time per direction (D15). Diagnostic only: a static profile
cannot apply them (the driver does not know the previous flick's intent).

For each used trial with context.previous:
    delta = theta - theta_prev wrapped to [-pi, pi]
    class = same (|delta| < 45 deg) | orthogonal | reversal (|delta| > 135 deg)
    residual = along_primary - b_model(theta)   (b_model = fitted Fourier bias, or 0)
Removing the static model first keeps direction imbalance between classes from showing up as a
sequential effect. Reported: per-class n / mean / SE, reversal - same with SE (Welch), and the mean
residual in 8 signed-delta bins (centred on 0, 45, ... deg, as sector_of(delta, 8)).

Reaction time per direction sector (default 8): timing.reaction_time_us (from first on-screen frame
when available, see reaction_time_basis); n / mean / median.
"""
import math

import numpy as np

from .geometry import TWO_PI, along_across, direction, sector_of
from .synth import fourier_eval

CLASSES = ("same", "orthogonal", "reversal")


def _summ(v):
    v = np.asarray(v, dtype=np.float64)
    n = len(v)
    return {"n": n, "mean": float(v.mean()) if n else None,
            "se": float(v.std(ddof=1) / math.sqrt(n)) if n >= 2 else None}


def sequential_effects(records, coeffs=None, same_max_deg=45.0, reversal_min_deg=135.0,
                       delta_bins=8, rt_sectors=8, max_abs_along_error=0.5):
    groups = {c: [] for c in CLASSES}
    dbins = [[] for _ in range(delta_bins)]
    rts = [[] for _ in range(rt_sectors)]
    for r in records:
        if r.get("excluded_reason"):
            continue
        g = r["geometry"]
        s = (g["start"]["x_counts"], g["start"]["y_counts"])
        t = (g["target"]["x_counts"], g["target"]["y_counts"])
        theta = direction(t[0] - s[0], t[1] - s[1])
        rt = r["timing"].get("reaction_time_us")
        if rt is not None and rt > 0:
            rts[sector_of(theta, rt_sectors)].append(rt)
        prev = (r.get("context") or {}).get("previous")
        p = r["outcome"].get("endpoint_primary")
        if prev is None or p is None:
            continue
        along, _ = along_across(s, t, (p["x_counts"], p["y_counts"]))
        if abs(along) > max_abs_along_error:
            continue
        resid = along - (float(fourier_eval(coeffs, theta)) if coeffs is not None else 0.0)
        delta = math.remainder(theta - prev["direction_theta"], TWO_PI)
        ad = abs(math.degrees(delta))
        cls = "same" if ad < same_max_deg else ("reversal" if ad > reversal_min_deg else "orthogonal")
        groups[cls].append(resid)
        dbins[sector_of(delta, delta_bins)].append(resid)
    classes = {c: _summ(groups[c]) for c in CLASSES}
    rs, ss = classes["reversal"], classes["same"]
    if rs["se"] is not None and ss["se"] is not None:
        diff = {"diff": rs["mean"] - ss["mean"], "se": math.sqrt(rs["se"] ** 2 + ss["se"] ** 2)}
    else:
        diff = None
    return {
        "classes": classes,
        "reversal_minus_same": diff,
        "by_delta": [dict(_summ(dbins[k]), delta=TWO_PI * k / delta_bins) for k in range(delta_bins)],
        "reaction_time": [{"sector": k, "n": len(rts[k]),
                           "mean_us": float(np.mean(rts[k])) if rts[k] else None,
                           "median_us": float(np.median(rts[k])) if rts[k] else None} for k in range(rt_sectors)],
    }
