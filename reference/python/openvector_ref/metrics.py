"""Per-trial metrics and per-direction Fitts throughput (mirrors OpenVector.Core.Analysis).

Throughput (ISO 9241-411 / Soukoreff & MacKenzie 2004), computed per direction sector
(default 8, centred on 2*pi*s/8) and amplitude condition (condition.amplitude_index, else 0):
    dx_i = (F_i - S_i).u_i - A_i          along-axis deviation of the FINAL endpoint (counts)
    We   = 4.133 * SD(dx)                 effective width (sample SD, ddof = 1)
    Ae   = mean((F_i - S_i).u_i)          effective amplitude
    IDe  = log2(Ae / We + 1)
    MT   = mean(t_final_click - t_onset)  seconds
    TP   = IDe / MT                        bits/s
Sector TP is the mean of its condition TPs ("mean of means"); overall TP is the mean over all
valid cells. Cells need >= min_trials trials and SD > 0.
"""
import math

import numpy as np

from .geometry import along_across, direction, sector_of


def trial_metrics(rec):
    """Dict of metrics or None if the trial has no usable geometry/final endpoint."""
    if rec.get("excluded_reason"):
        return None
    g = rec["geometry"]
    s = (g["start"]["x_counts"], g["start"]["y_counts"])
    t = (g["target"]["x_counts"], g["target"]["y_counts"])
    amp = math.hypot(t[0] - s[0], t[1] - s[1])
    if amp <= 0:
        return None
    out = rec["outcome"]
    tm = rec["timing"]
    kin = rec["kinematics"]
    m = {"theta": direction(t[0] - s[0], t[1] - s[1]), "amplitude_counts": amp,
         "amplitude_index": (rec.get("condition") or {}).get("amplitude_index", 0),
         "weight": rec["provenance"]["confidence"]}
    for key, name in (("endpoint_primary", "primary"), ("endpoint_final", "final")):
        p = out.get(key)
        if p is None:
            m[f"along_{name}"] = m[f"across_{name}"] = None
        else:
            a, c = along_across(s, t, (p["x_counts"], p["y_counts"]))
            m[f"along_{name}"], m[f"across_{name}"] = a, c
    m["overshoot_ratio"] = None if m["along_primary"] is None else 1.0 + m["along_primary"]
    onset, click = tm.get("t_onset_us"), tm.get("t_final_click_us")
    m["movement_time_us"] = (click - onset) if onset is not None and click is not None else tm.get("movement_time_us")
    pe = tm.get("t_primary_end_us")
    m["primary_duration_us"] = (pe - onset) if onset is not None and pe is not None else tm.get("primary_duration_us")
    f = out.get("endpoint_final")
    pl = kin.get("path_length_counts")
    if f is not None and pl:
        m["path_efficiency"] = math.hypot(f["x_counts"] - s[0], f["y_counts"] - s[1]) / pl
    else:
        m["path_efficiency"] = None
    m["n_corrections"] = kin.get("n_corrections")
    m["peak_speed_counts_per_s"] = kin.get("peak_speed_counts_per_s")
    return m


def throughput(metrics, direction_sectors=8, min_trials=3):
    cells = {}
    for m in metrics:
        if m is None or m["along_final"] is None or not m["movement_time_us"] or m["movement_time_us"] <= 0:
            continue
        sec = sector_of(m["theta"], direction_sectors)
        a = m["amplitude_counts"]
        cells.setdefault((sec, m["amplitude_index"]), []).append(
            (m["along_final"] * a, (1.0 + m["along_final"]) * a, m["movement_time_us"] * 1e-6))
    cell_out = []
    for key in sorted(cells):
        v = cells[key]
        if len(v) < min_trials:
            continue
        dev = np.array([x[0] for x in v])
        sd = float(np.std(dev, ddof=1))
        if sd <= 0:
            continue
        we = 4.133 * sd
        ae = float(np.mean([x[1] for x in v]))
        ide = math.log2(ae / we + 1.0)
        mt = float(np.mean([x[2] for x in v]))
        cell_out.append({"sector": key[0], "amplitude_index": key[1], "n": len(v), "we": we, "ae": ae,
                         "ide": ide, "mt_s": mt, "tp": ide / mt})
    sectors = []
    for s in range(direction_sectors):
        cs = [c for c in cell_out if c["sector"] == s]
        sectors.append({"sector": s, "n": sum(c["n"] for c in cs),
                        "tp": float(np.mean([c["tp"] for c in cs])) if cs else None})
    overall = float(np.mean([c["tp"] for c in cell_out])) if cell_out else None
    return {"overall_tp": overall, "sectors": sectors, "cells": cell_out}
