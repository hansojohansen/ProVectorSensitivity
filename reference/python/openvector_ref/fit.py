"""Reference gain-map fitter (mirrors OpenVector.Core.Analysis.GainMapEstimator).

Signal. Per used trial i: theta_i = atan2 of target - start (counts) and the relative along-axis
error of the PRIMARY submovement endpoint, e_i = (P - S).u / A - 1. The primary submovement is the
open-loop part of the flick, so it carries the user's gain error; final endpoints are always on
target because corrections absorb it (Meyer et al. 1988; research B §4.2).

Model. b(theta) = E[e | theta] is a Fourier series of order K:
    b(theta) = c0 + sum_{k=1..K} a_k cos(k theta) + b_k sin(k theta)
estimated by penalised weighted least squares
    min_c  sum_i w_i (e_i - x_i.c)^2 + lam * sum_k k^4 (a_k^2 + b_k^2)
(w_i = provenance confidence; the k^4 penalty is the discrete roughness of f''; c0 is free).

Compensating gain. If the user's open-loop output in direction theta is (1 + b(theta)) times the
intended displacement, and the user does NOT adapt to the new gain, scaling counts by
g(theta) = 1 / (1 + b(theta)) makes the expected primary endpoint land on target. Clamped to bounds.

Sparse strata (D9 revision). Trials are counted (sum of weights) per 32 sectors centred on
2*pi*s/32. For a sector with weight n_s < N_min (20), N_min - n_s pseudo-trials are added at its
centre with value t_s = 1/d-weighted mean of dense sectors within +-2 sectors, or 0 (= gain 1.0)
if there are none. A sector's effective mean is thus (n_s mean_s + (N_min - n_s) t_s) / N_min.

Order selection. K in 0..min(8, (n/folds - 1)//2) by 10-fold CV (fold = index mod 10; pseudo-trials
always in training), choosing the K with the lowest CV error (option: 1-SE rule, which is more
conservative but missed real 1-4 % harmonics in synthetic tests).

CIs. Nonparametric bootstrap over used trials (SplitMix64(seed), index = next_int(n)), refitting
with the selected K and recomputed pseudo-trials; percentile 2.5/97.5 (numpy 'linear') of the
clamped gain at each display bin.
"""
from dataclasses import dataclass, asdict
import math

import numpy as np

from .geometry import TWO_PI, along_across, direction, sector_of, sector_center
from .rng import SplitMix64


@dataclass
class FitOptions:
    max_order: int = 8
    lam: float = 0.05
    strata: int = 32
    min_per_stratum: float = 20.0
    neighbour_reach: int = 2
    folds: int = 10
    min_gain: float = 0.85
    max_gain: float = 1.15
    max_abs_along_error: float = 0.5
    bootstrap_reps: int = 200
    seed: int = 12345
    display_bins: int = 64
    signal: str = "primary"
    order_rule: str = "min"   # "min" = lowest CV error; "1se" = smallest order within 1 SE of it


def extract_signal(records, opts: FitOptions):
    """Return (theta, e, w) arrays of used trials, in record order."""
    th, ee, ww = [], [], []
    key = "endpoint_primary" if opts.signal == "primary" else "endpoint_final"
    for r in records:
        if r.get("excluded_reason"):
            continue
        prov = r["provenance"]
        if prov["target_truth"] == "unknown" or prov["confidence"] <= 0:
            continue
        p = r["outcome"].get(key)
        if p is None:
            continue
        g = r["geometry"]
        s = (g["start"]["x_counts"], g["start"]["y_counts"])
        t = (g["target"]["x_counts"], g["target"]["y_counts"])
        if math.hypot(t[0] - s[0], t[1] - s[1]) <= 0:
            continue
        along, _ = along_across(s, t, (p["x_counts"], p["y_counts"]))
        if abs(along) > opts.max_abs_along_error:
            continue
        th.append(direction(t[0] - s[0], t[1] - s[1]))
        ee.append(along)
        ww.append(float(prov["confidence"]))
    return np.array(th), np.array(ee), np.array(ww)


def design(theta, order):
    theta = np.atleast_1d(np.asarray(theta, dtype=np.float64))
    X = np.empty((len(theta), 2 * order + 1))
    X[:, 0] = 1.0
    for k in range(1, order + 1):
        X[:, 2 * k - 1] = np.cos(k * theta)
        X[:, 2 * k] = np.sin(k * theta)
    return X


def penalty(order, lam):
    p = np.zeros(2 * order + 1)
    for k in range(1, order + 1):
        p[2 * k - 1] = p[2 * k] = lam * k ** 4
    return p


def pseudo_observations(theta, e, w, opts: FitOptions):
    S = opts.strata
    nw = np.zeros(S)
    sw = np.zeros(S)
    for t, y, wi in zip(theta, e, w):
        s = sector_of(t, S)
        nw[s] += wi
        sw[s] += wi * y
    dense = nw >= opts.min_per_stratum
    pt, pe, pw, sparse = [], [], [], []
    for s in range(S):
        if dense[s]:
            continue
        num = den = 0.0
        for d in range(1, opts.neighbour_reach + 1):
            for side in (-1, 1):
                q = (s + side * d) % S
                if dense[q]:
                    num += (sw[q] / nw[q]) / d
                    den += 1.0 / d
        pt.append(sector_center(s, S))
        pe.append(num / den if den > 0 else 0.0)
        pw.append(opts.min_per_stratum - nw[s])
        sparse.append(s)
    return np.array(pt), np.array(pe), np.array(pw), sparse


def solve(theta, e, w, order, lam):
    """Penalised WLS. Returns (coeffs, inverse of the penalised normal matrix)."""
    X = design(theta, order)
    A = X.T @ (X * w[:, None]) + np.diag(penalty(order, lam))
    b = X.T @ (w * e)
    c = np.linalg.solve(A, b)
    return c, np.linalg.inv(A)


def gain(coeffs, theta, min_gain, max_gain):
    f = design(theta, (len(coeffs) - 1) // 2) @ np.asarray(coeffs)
    den = 1.0 + f
    g = np.where(den > 0, 1.0 / np.where(den > 0, den, 1.0), np.inf)
    return np.clip(g, min_gain, max_gain)


def _fit_fixed(theta, e, w, order, opts):
    pt, pe, pw, _ = pseudo_observations(theta, e, w, opts)
    c, _ = solve(np.concatenate([theta, pt]), np.concatenate([e, pe]), np.concatenate([w, pw]), order, opts.lam)
    return c


def cross_validate(theta, e, w, opts: FitOptions):
    n = len(theta)
    folds = opts.folds
    kmax = min(opts.max_order, max(0, (n // folds - 1) // 2))
    if n < folds:
        return 0, [], []
    pt, pe, pw, _ = pseudo_observations(theta, e, w, opts)
    fold = np.arange(n) % folds
    cv, se = [], []
    for K in range(kmax + 1):
        scores = []
        for f in range(folds):
            tr, te = fold != f, fold == f
            c, _ = solve(np.concatenate([theta[tr], pt]), np.concatenate([e[tr], pe]),
                         np.concatenate([w[tr], pw]), K, opts.lam)
            r = e[te] - design(theta[te], K) @ c
            scores.append(float(np.sum(w[te] * r * r) / np.sum(w[te])))
        scores = np.array(scores)
        cv.append(float(scores.mean()))
        se.append(float(scores.std(ddof=1) / math.sqrt(len(scores))))
    best = int(np.argmin(cv))
    if opts.order_rule == "1se":
        limit = cv[best] + se[best]
        best = next(K for K in range(kmax + 1) if cv[K] <= limit)
    return best, cv, se


def percentile_linear(sorted_vals, p):
    """numpy.percentile(method='linear') on an already sorted 1-D array (same lerp as numpy)."""
    n = len(sorted_vals)
    h = (p / 100.0) * (n - 1)
    lo = int(math.floor(h))
    hi = min(lo + 1, n - 1)
    t = h - lo
    a, b = sorted_vals[lo], sorted_vals[hi]
    d = b - a
    return b - d * (1 - t) if t >= 0.5 else a + d * t


def fit(theta, e, w, opts: FitOptions = None, n_trials=None):
    opts = opts or FitOptions()
    theta, e, w = (np.asarray(a, dtype=np.float64) for a in (theta, e, w))
    n = len(theta)
    order, cv, cv_se = cross_validate(theta, e, w, opts)
    pt, pe, pw, sparse = pseudo_observations(theta, e, w, opts)
    c, Ainv = solve(np.concatenate([theta, pt]), np.concatenate([e, pe]), np.concatenate([w, pw]), order, opts.lam)
    p = 2 * order + 1
    r = e - design(theta, order) @ c
    dof = max(np.sum(w) - p, 1.0)
    sigma2 = float(np.sum(w * r * r) / dof)
    cov = sigma2 * Ainv

    B = opts.display_bins
    bin_theta = np.array([sector_center(k, B) for k in range(B)])
    bin_n = np.zeros(B, dtype=int)
    for t in theta:
        bin_n[sector_of(t, B)] += 1
    g = gain(c, bin_theta, opts.min_gain, opts.max_gain)

    lo, hi = g.copy(), g.copy()
    if opts.bootstrap_reps > 0 and n > 0:
        rng = SplitMix64(opts.seed)
        G = np.empty((opts.bootstrap_reps, B))
        for rep in range(opts.bootstrap_reps):
            idx = np.array([rng.next_int(n) for _ in range(n)], dtype=np.int64)
            cb = _fit_fixed(theta[idx], e[idx], w[idx], order, opts)
            G[rep] = gain(cb, bin_theta, opts.min_gain, opts.max_gain)
        G.sort(axis=0)
        lo = np.array([percentile_linear(G[:, k], 2.5) for k in range(B)])
        hi = np.array([percentile_linear(G[:, k], 97.5) for k in range(B)])

    return {
        "order": order,
        "coeffs": c.tolist(),
        "cov": cov.tolist(),
        "cv_scores": cv,
        "cv_se": cv_se,
        "residual_sd": math.sqrt(sigma2),
        "n_trials": n if n_trials is None else n_trials,
        "n_used": n,
        "sparse_strata": sparse,
        "bins": [{"theta": float(bin_theta[k]), "gain": float(g[k]), "ci_low": float(lo[k]),
                  "ci_high": float(hi[k]), "n": int(bin_n[k])} for k in range(B)],
        "options": asdict(opts),
    }


def fit_records(records, opts: FitOptions = None):
    opts = opts or FitOptions()
    th, e, w = extract_signal(records, opts)
    return fit(th, e, w, opts, n_trials=len(records))


def sample_for_projection(coeffs, bins, n, min_gain, max_gain, kappa=26.0, floor=0.05):
    """(theta[], gain[], weight[]) on theta_j = 2 pi j / n (D14).

    weight = circular kernel density of trial directions from the display-bin counts
    (von Mises kernel, kappa = 26 ~ one 32-sector SD), normalised to mean 1, plus a 5 % floor so
    that no direction is completely unconstrained: w = (d / mean(d) + floor) / (1 + floor).
    """
    theta = np.array([TWO_PI * j / n for j in range(n)])
    g = gain(coeffs, theta, min_gain, max_gain)
    dens = np.zeros(n)
    for b in bins:
        if b["n"] > 0:
            dens += b["n"] * np.exp(kappa * (np.cos(theta - b["theta"]) - 1.0))
    if dens.sum() <= 0:
        w = np.ones(n)
    else:
        w = (dens / dens.mean() + floor) / (1.0 + floor)
    return theta, g, w
