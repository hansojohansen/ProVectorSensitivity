"""Velocity-based submovement segmentation (mirrors OpenVector.Core.Analysis.Segmenter).

Input: raw, unaccumulated per-packet counts (t_us, dx, dy) for one trial, starting at the stimulus.

Steps (defaults in SegmentationOptions):
 1. Packets with identical timestamps are merged (batch timestamps). Position is the cumulative sum.
 2. Continuous position by linear interpolation between packet times. A packet's counts are
    assumed to accrue over one median packet interval before it (so idle gaps do not smear
    motion): an extra knot (t_i - dt_med, previous position) is inserted when the gap exceeds
    2*dt_med, and one before the first packet (and at t_ref, the stimulus, if earlier).
 3. Resample on a 1 ms grid; velocity by central differences; Gaussian smoothing per axis
    (sigma = 5 ms, truncated at 3 sigma, kernel renormalised at the edges); speed = |v|.
 4. Threshold thr = max(5 % of peak speed, 20 counts/s). Onset = first sample of the
    above-threshold run that contains the peak.
 5. Walking forward from onset, a submovement ends when
      a) speed drops below thr (a stop), or
      b) at a local speed minimum s_min < 0.6 * min(peak so far in this submovement,
         peak in the next 100 ms before the next stop) (a dip between two velocity peaks), or
      c) the along-target velocity changes sign while s < 0.5 * peak so far (a reversal).
    Cases b/c start the next submovement at the same sample; case a starts it at the next
    above-threshold sample.
 6. Segments that start before the global peak are merged into the primary submovement
    (pre-peak hesitations are not corrections). Later segments shorter than 8 ms or with
    |displacement| < 1 count are dropped. n_corrections = remaining segments - 1.
"""
from dataclasses import dataclass, field
import math

import numpy as np


@dataclass
class SegmentationOptions:
    grid_us: int = 1000
    smoothing_sigma_ms: float = 5.0
    onset_fraction: float = 0.05
    min_speed_counts_per_s: float = 20.0
    dip_ratio: float = 0.6
    reversal_ratio: float = 0.5
    lookahead_ms: float = 100.0
    min_submovement_ms: float = 8.0
    min_submovement_counts: float = 1.0


@dataclass
class Submovement:
    t_start_us: int
    t_end_us: int
    dx: float
    dy: float
    peak_speed: float


@dataclass
class SegmentationResult:
    t_onset_us: int
    t_peak_us: int
    t_primary_end_us: int
    t_end_us: int
    peak_speed: float
    primary_end: tuple
    final_end: tuple
    path_length: float
    submovements: list = field(default_factory=list)

    @property
    def n_corrections(self):
        return len(self.submovements) - 1


def _resample(t_us, dx, dy, t_ref, grid_us):
    # 1. merge identical timestamps
    ts, xs, ys = [], [], []
    for t, a, b in zip(t_us, dx, dy):
        if ts and t == ts[-1]:
            xs[-1] += a
            ys[-1] += b
        else:
            ts.append(int(t)); xs.append(int(a)); ys.append(int(b))
    n = len(ts)
    if n >= 2:
        dt_med = int(round(float(np.median(np.diff(np.array(ts, dtype=np.int64))))))
    else:
        dt_med = 1000
    dt_med = max(dt_med, 1)
    # 2. knots
    kt, kx, ky = [], [], []
    first = ts[0] - dt_med
    if t_ref is not None and t_ref < first:
        kt.append(int(t_ref)); kx.append(0.0); ky.append(0.0)
    kt.append(first); kx.append(0.0); ky.append(0.0)
    cx = cy = 0
    for i in range(n):
        if i > 0 and ts[i] - ts[i - 1] > 2 * dt_med:
            kt.append(ts[i] - dt_med); kx.append(float(cx)); ky.append(float(cy))
        cx += xs[i]; cy += ys[i]
        kt.append(ts[i]); kx.append(float(cx)); ky.append(float(cy))
    # 3. grid
    g0 = kt[0]
    count = int(math.ceil((kt[-1] - g0) / grid_us)) + 1
    tau = g0 + grid_us * np.arange(count, dtype=np.int64)
    kt_a = np.array(kt, dtype=np.float64)
    x = np.interp(tau.astype(np.float64), kt_a, np.array(kx))
    y = np.interp(tau.astype(np.float64), kt_a, np.array(ky))
    return tau, x, y


def _smooth(v, sigma):
    r = int(math.ceil(3.0 * sigma))
    j = np.arange(-r, r + 1, dtype=np.float64)
    w = np.exp(-0.5 * (j / sigma) ** 2)
    num = np.convolve(v, w, mode="same") if len(v) >= len(w) else _conv_same_small(v, w)
    den = np.convolve(np.ones_like(v), w, mode="same") if len(v) >= len(w) else _conv_same_small(np.ones_like(v), w)
    return num / den


def _conv_same_small(v, w):
    r = len(w) // 2
    out = np.zeros_like(v)
    for k in range(len(v)):
        acc = 0.0
        for jj in range(-r, r + 1):
            if 0 <= k + jj < len(v):
                acc += w[jj + r] * v[k + jj]
        out[k] = acc
    return out


def segment(t_us, dx, dy, t_ref=None, direction=None, opts: SegmentationOptions = None):
    """Segment one trial. Returns SegmentationResult or None if there is no movement.

    direction: target direction theta (rad) for the along-velocity reversal rule; if None the
    net displacement direction is used. Positions are relative to the trial origin (the
    position at the first knot), in counts.
    """
    opts = opts or SegmentationOptions()
    if len(t_us) == 0:
        return None
    tau, x, y = _resample(t_us, dx, dy, t_ref, opts.grid_us)
    n = len(tau)
    if n < 3:
        return None
    dt = opts.grid_us * 1e-6
    vx = np.gradient(x, dt)
    vy = np.gradient(y, dt)
    sigma = opts.smoothing_sigma_ms * 1000.0 / opts.grid_us
    vx = _smooth(vx, sigma)
    vy = _smooth(vy, sigma)
    s = np.hypot(vx, vy)
    if direction is None:
        direction = math.atan2(y[-1], x[-1])
    ux, uy = math.cos(direction), math.sin(direction)
    va = vx * ux + vy * uy

    kp = int(np.argmax(s))
    peak = float(s[kp])
    if peak <= opts.min_speed_counts_per_s:
        return None
    thr = max(opts.onset_fraction * peak, opts.min_speed_counts_per_s)
    look = int(round(opts.lookahead_ms * 1000.0 / opts.grid_us))

    k0 = kp
    while k0 > 0 and s[k0 - 1] >= thr:
        k0 -= 1

    def right_peak(k):
        m = s[k]
        j = k + 1
        while j < n and j <= k + look and s[j] >= thr:
            if s[j] > m:
                m = s[j]
            j += 1
        return m

    segs = []  # (start, end, peak)
    in_move, seg_start, seg_peak = True, k0, s[k0]
    for k in range(k0 + 1, n):
        if in_move:
            if s[k] < thr:
                segs.append((seg_start, k, seg_peak))
                in_move = False
                continue
            boundary = False
            if k + 1 < n and s[k] <= s[k - 1] and s[k] < s[k + 1]:
                if s[k] < opts.dip_ratio * min(seg_peak, right_peak(k)):
                    boundary = True
            if not boundary and ((va[k - 1] > 0 >= va[k]) or (va[k - 1] < 0 <= va[k])) \
                    and s[k] < opts.reversal_ratio * seg_peak:
                boundary = True
            if boundary:
                segs.append((seg_start, k, seg_peak))
                seg_start, seg_peak = k, s[k]
            elif s[k] > seg_peak:
                seg_peak = s[k]
        elif s[k] >= thr:
            in_move, seg_start, seg_peak = True, k, s[k]
    if in_move:
        segs.append((seg_start, n - 1, seg_peak))

    # 6. merge pre-peak segments into the primary, drop tiny corrections
    p = 0
    while p < len(segs) and segs[p][1] < kp:
        p += 1
    primary = (segs[0][0], segs[p][1], max(sg[2] for sg in segs[: p + 1]))
    kept = [primary]
    min_len = opts.min_submovement_ms * 1000.0 / opts.grid_us
    for (a, b, pk) in segs[p + 1:]:
        if (b - a) < min_len or math.hypot(x[b] - x[a], y[b] - y[a]) < opts.min_submovement_counts:
            continue
        kept.append((a, b, pk))

    subs = [Submovement(int(tau[a]), int(tau[b]), float(x[b] - x[a]), float(y[b] - y[a]), float(pk)) for a, b, pk in kept]
    pe = primary[1]
    last = kept[-1][1]
    path_length = float(np.sum(np.hypot(np.diff(x), np.diff(y))))
    return SegmentationResult(
        t_onset_us=int(tau[k0]), t_peak_us=int(tau[kp]), t_primary_end_us=int(tau[pe]),
        t_end_us=int(tau[last]), peak_speed=peak,
        primary_end=(float(x[pe]), float(y[pe])), final_end=(float(x[last]), float(y[last])),
        path_length=path_length, submovements=subs)
