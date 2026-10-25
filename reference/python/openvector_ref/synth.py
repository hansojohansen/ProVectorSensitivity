"""Synthetic users for testing the calibration pipeline end to end.

Motor model (per trial, all in raw counts, D14 coordinates):
  * The user intends to move from S to T (amplitude A, direction theta).
  * Primary (open-loop) submovement: displacement
        A * [(1 + b(theta) + r(A) + e_a) u + e_c n],   e_a ~ N(0, sigma_along), e_c ~ N(0, sigma_across)
    where b(theta) is the user's direction-dependent gain bias (a Fourier series up to order 4 here)
    and r(A) = range_coef * ln(A / amp_ref) is an optional range effect (short overshoot, long undershoot
    for range_coef < 0). Its profile is minimum-jerk with duration 0.10 + 0.06 * log2(1 + A / W) s.
  * Corrective submovements while the cursor is outside the target (max 3): remaining error r is
    corrected with the same directional bias b(theta_r) and smaller relative noise, after a dwell of
    30-90 ms or, with probability blend_prob, overlapping the end of the previous submovement by 5-15 %.
  * Optional diagnostic effects (D15; not representable by a static profile): reversal_bias is
    added to the primary extent when the flick reverses the previous direction (|dtheta| > 135 deg);
    reaction_up_extra_s adds reaction time proportional to the upward component.
  * The target appears on screen 2-6 ms after it is requested (display delay); reaction time is
    measured from the on-screen time.
  * Packets are sampled at polling_hz with integer counts (remainder carried by rounding the
    cumulative position) and only emitted when non-zero, like a real mouse.

The compensating gain the fitter should recover is g(theta) = 1 / (1 + b(theta) + mean r(A)).
"""
from dataclasses import dataclass
import math

import numpy as np

from .geometry import TWO_PI, along_across, direction, sector_of
from .segment import segment

SCHEMA_VERSION = "1.0.0"


def fourier_eval(coeffs, theta):
    """f(theta) = c0 + sum_k a_k cos(k theta) + b_k sin(k theta), coeffs = [c0, a1, b1, a2, b2, ...]."""
    theta = np.asarray(theta, dtype=np.float64)
    coeffs = list(coeffs) + [0.0] * (1 - len(coeffs) % 2)  # pad to odd length (trailing cos term)
    out = np.full_like(theta, coeffs[0], dtype=np.float64)
    for k in range(1, (len(coeffs) - 1) // 2 + 1):
        out = out + coeffs[2 * k - 1] * np.cos(k * theta) + coeffs[2 * k] * np.sin(k * theta)
    return out


@dataclass
class SyntheticUser:
    bias_coeffs: tuple = (0.0,)          # Fourier coeffs of b(theta)
    sigma_along: float = 0.10
    sigma_across: float = 0.05
    range_coef: float = 0.0
    amp_ref_counts: float = 600.0
    corr_sigma_along: float = 0.10
    corr_sigma_across: float = 0.05
    blend_prob: float = 0.15
    reaction_mean_s: float = 0.20
    polling_hz: float = 1000.0
    reversal_bias: float = 0.0
    reaction_up_extra_s: float = 0.0

    def bias(self, theta):
        return fourier_eval(self.bias_coeffs, theta)

    def true_gain(self, theta, mean_range=0.0):
        return 1.0 / (1.0 + self.bias(theta) + mean_range)


# --- a few named users used by tests/golden vectors ---------------------------------------------
def user_identity(**kw):
    return SyntheticUser(bias_coeffs=(0.0,), **kw)


def user_up_undershoot(**kw):
    """-8 % extent (undershoot) when flicking up (theta = 3pi/2), 0 when flicking down.
    b = -0.08 * ((1 - sin t)/2)^2 = -0.03 + 0.04 sin t + 0.01 cos 2t."""
    return SyntheticUser(bias_coeffs=(-0.03, 0.0, 0.04, 0.01, 0.0), **kw)


def user_diagonal(**kw):
    """sin 2theta bias (the '/' vs '\\' asymmetry Raw Accel 1.7 cannot express): +5 % at theta=pi/4."""
    return SyntheticUser(bias_coeffs=(0.0, 0.0, 0.0, 0.0, 0.05), **kw)


def user_left_right(**kw):
    """Left/right asymmetry: +4 % overshoot to the right, -4 % to the left."""
    return SyntheticUser(bias_coeffs=(0.0, 0.04), **kw)


def user_rich(**kw):
    """Harmonics up to order 4 including sin 2theta."""
    return SyntheticUser(bias_coeffs=(-0.01, 0.03, -0.02, 0.015, 0.04, -0.01, 0.01, 0.0, 0.012), **kw)


# --- trajectory -----------------------------------------------------------------------------------
def _min_jerk(tau):
    tau = np.clip(tau, 0.0, 1.0)
    return 10 * tau ** 3 - 15 * tau ** 4 + 6 * tau ** 5


def _simulate(user: SyntheticUser, theta, amp, radius, rng, prev_theta=None):
    """Return list of submovements (t0_s, T_s, dx, dy), true primary endpoint, click time (s), mean range."""
    u = np.array([math.cos(theta), math.sin(theta)])
    nrm = np.array([-u[1], u[0]])
    target = amp * u
    r_eff = user.range_coef * math.log(amp / user.amp_ref_counts) if user.range_coef else 0.0
    m = 1.0 + float(user.bias(theta)) + r_eff + rng.normal(0, user.sigma_along)
    if prev_theta is not None and user.reversal_bias:
        d = abs(math.remainder(theta - prev_theta, TWO_PI))
        if d > 0.75 * math.pi:
            m += user.reversal_bias
    disp = amp * (m * u + rng.normal(0, user.sigma_across) * nrm)
    t0 = max(0.12, rng.normal(user.reaction_mean_s, 0.03)) + user.reaction_up_extra_s * max(0.0, -math.sin(theta))
    T = 0.10 + 0.06 * math.log2(1 + amp / (2 * radius))
    subs = [(t0, T, disp[0], disp[1])]
    pos = disp.copy()
    primary = pos.copy()
    for _ in range(3):
        rem = target - pos
        dist = float(np.hypot(*rem))
        if dist <= radius:
            break
        th_r = math.atan2(rem[1], rem[0])
        ur = rem / dist
        nr = np.array([-ur[1], ur[0]])
        mc = 1.0 + float(user.bias(th_r)) + rng.normal(0, user.corr_sigma_along)
        cd = dist * (mc * ur + rng.normal(0, user.corr_sigma_across) * nr)
        prev_t0, prev_T = subs[-1][0], subs[-1][1]
        if rng.random() < user.blend_prob:
            start = prev_t0 + prev_T * (1.0 - rng.uniform(0.05, 0.15))
        else:
            start = prev_t0 + prev_T + rng.uniform(0.03, 0.09)
        Tc = 0.07 + 0.05 * math.log2(1 + dist / (2 * radius))
        subs.append((start, Tc, cd[0], cd[1]))
        pos = pos + cd
    end = max(s[0] + s[1] for s in subs)
    t_click = end + 0.03
    return subs, primary, t_click, r_eff


def _sample_packets(subs, t_click, polling_hz):
    """Packets (t_us relative to stimulus, dx, dy), integer counts, zero packets dropped."""
    period = 1e6 / polling_hz
    k = np.arange(1, int(math.ceil((t_click * 1e6 + 20000) / period)) + 1)
    t_us = np.round(k * period).astype(np.int64)
    ts = t_us * 1e-6
    px = np.zeros(len(ts)); py = np.zeros(len(ts))
    for (t0, T, dx, dy) in subs:
        f = _min_jerk((ts - t0) / T)
        px += f * dx; py += f * dy
    cx = np.round(px).astype(np.int64)
    cy = np.round(py).astype(np.int64)
    ddx = np.diff(np.concatenate([[0], cx]))
    ddy = np.diff(np.concatenate([[0], cy]))
    keep = (ddx != 0) | (ddy != 0)
    return t_us[keep], ddx[keep], ddy[keep], (t_us, cx, cy)


def generate_session(user: SyntheticUser, plan, seed, amplitudes_counts=(230.0, 680.0, 1360.0),
                     radius_counts=(45.0, 60.0, 75.0), yaw_deg_per_count=0.022, session_id="synthetic-session",
                     include_path=True, sector_count=32, block_size=96, break_us=30_000_000):
    """Simulate one calibration session following `plan` (from plan.generate_plan).

    Returns (records, truths): MovementTrial v1 dicts and per-trial ground truth.
    """
    rng = np.random.default_rng(seed)
    records, truths = [], []
    t_clock = 1_000_000
    prev = None
    last_break_end = t_clock
    for item in plan:
        if item["index"] > 0 and item["index"] % block_size == 0:
            t_clock += break_us
            last_break_end = t_clock
        a_idx = item["amplitude_index"]
        amp = float(amplitudes_counts[a_idx])
        radius = float(radius_counts[a_idx])
        theta = item["theta"]
        delay = rng.uniform(0.002, 0.006)  # request -> first frame on screen
        subs, primary, t_click, r_eff = _simulate(user, theta, amp, radius, rng,
                                                  prev_theta=prev["theta"] if prev else None)
        subs = [(t0 + delay, T, ddx, ddy) for (t0, T, ddx, ddy) in subs]
        t_click += delay
        t_rel, dx, dy, (t_all, cx_all, cy_all) = _sample_packets(subs, t_click, user.polling_hz)
        t_stim = t_clock
        t_abs = t_rel + t_stim
        click_us = t_stim + int(round(t_click * 1e6))
        # cumulative counts at the click
        idx = np.searchsorted(t_all + t_stim, click_us, side="right") - 1
        fx, fy = (float(cx_all[idx]), float(cy_all[idx])) if idx >= 0 else (0.0, 0.0)
        in_click = t_abs <= click_us
        seg = segment(t_abs[in_click].tolist(), dx[in_click].tolist(), dy[in_click].tolist(),
                      t_ref=t_stim, direction=theta)
        target = (amp * math.cos(theta), amp * math.sin(theta))
        hit = math.hypot(fx - target[0], fy - target[1]) <= radius
        rec = make_record(item, session_id, theta, amp, radius, target, seg, (fx, fy), hit,
                          t_stim, click_us, yaw_deg_per_count, user.polling_hz, sector_count)
        on_screen = t_stim + int(round(delay * 1e6))
        rec["timing"]["t_stimulus_on_screen_us"] = on_screen
        if seg:
            rec["timing"]["reaction_time_us"] = seg.t_onset_us - on_screen
            rec["timing"]["reaction_time_basis"] = "on_screen"
        rec["condition"]["block"] = item["index"] // block_size
        rec["context"] = {
            "trial_index_session": item["index"],
            "trial_index_block": item["index"] % block_size,
            "block": item["index"] // block_size,
            "previous": None if prev is None else {
                "trial_id": prev["trial_id"], "direction_theta": prev["theta"], "amplitude_counts": prev["amp"],
                "hit": prev["hit"], "excluded_reason": prev["excluded"]},
            "inter_trial_interval_us": None if prev is None else t_stim - prev["click"],
            "time_since_break_us": t_stim - last_break_end,
            "time_since_session_start_us": t_stim - 1_000_000,
        }
        prev = {"trial_id": rec["trial_id"], "theta": rec["geometry"]["direction_theta"], "amp": amp, "hit": bool(hit),
                "excluded": rec["excluded_reason"], "click": click_us}
        if include_path:
            rec["path"] = {"t_us": t_abs.tolist(), "dx": dx.tolist(), "dy": dy.tolist()}
        records.append(rec)
        truths.append({"theta": theta, "amplitude_counts": amp, "primary_true": [float(primary[0]), float(primary[1])],
                       "bias": float(user.bias(theta)), "range": r_eff, "n_submovements": len(subs)})
        t_clock = click_us + 600_000
    return records, truths


def make_record(item, session_id, theta, amp, radius, target, seg, final, hit, t_stim, click_us,
                yaw_deg_per_count, polling_hz, sector_count):
    def pt(x, y):
        return {"x_counts": x, "y_counts": y}

    start = (0.0, 0.0)
    rec = {
        "schema_version": SCHEMA_VERSION,
        "trial_id": f"{session_id}-{item['index']:04d}",
        "session_id": session_id,
        "source": {"kind": "synthetic", "version": "reference-python", "protocol_id": "ov-flick-32x3"},
        "provenance": {"target_truth": "measured", "confidence": 1.0},
        "device": {"name": "synthetic", "polling_hz_nominal": polling_hz, "dpi": 800},
        "mapping": {"yaw_deg_per_count": yaw_deg_per_count, "pitch_deg_per_count": yaw_deg_per_count,
                    "fov_h_deg": 103.0, "counts_stage": "post_driver"},
        "active_gain_map_id": "identity",
        "condition": {"trial_index": item["index"], "sector": item["sector"], "sector_count": sector_count,
                      "amplitude_index": item["amplitude_index"]},
        "geometry": {
            "start": pt(*start),
            "target": dict(pt(*target), radius_counts=radius, shape="sphere"),
            "amplitude_counts": amp,
            "amplitude_deg": amp * yaw_deg_per_count,
            "direction_theta": direction(target[0], target[1]),
        },
        "outcome": {
            "endpoint_primary": pt(*seg.primary_end) if seg else None,
            "endpoint_final": pt(*final),
            "hit": bool(hit),
            "shots_fired": 1,
            "clicks": [{"t_us": int(click_us), "x_counts": final[0], "y_counts": final[1], "hit": bool(hit)}],
        },
        "timing": {
            "clock": "qpc_us", "utc_offset_us": 0,
            "t_stimulus_us": int(t_stim),
            "t_onset_us": seg.t_onset_us if seg else None,
            "t_peak_velocity_us": seg.t_peak_us if seg else None,
            "t_primary_end_us": seg.t_primary_end_us if seg else None,
            "t_final_click_us": int(click_us),
            "reaction_time_us": (seg.t_onset_us - t_stim) if seg else None,
            "reaction_time_basis": "requested" if seg else None,
            "movement_time_us": (click_us - seg.t_onset_us) if seg else None,
            "primary_duration_us": (seg.t_primary_end_us - seg.t_onset_us) if seg else None,
        },
        "kinematics": {
            "peak_speed_counts_per_s": seg.peak_speed if seg else None,
            "peak_speed_deg_per_s": seg.peak_speed * yaw_deg_per_count if seg else None,
            "path_length_counts": seg.path_length if seg else None,
            "n_corrections": seg.n_corrections if seg else None,
            "submovements": [{"t_start_us": s.t_start_us, "t_end_us": s.t_end_us, "dx_counts": s.dx, "dy_counts": s.dy,
                              "peak_speed_counts_per_s": s.peak_speed} for s in seg.submovements] if seg else [],
            "segmentation": "ov-velocity-1",
        },
        "excluded_reason": None if seg else "segmentation_ambiguous",
    }
    if seg:
        ap, cp = along_across(start, target, seg.primary_end)
        af, cf = along_across(start, target, final)
        rec["outcome"]["errors"] = {"along_primary": ap, "across_primary": cp, "along_final": af, "across_final": cf}
    return rec
