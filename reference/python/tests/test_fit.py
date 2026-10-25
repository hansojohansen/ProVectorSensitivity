import numpy as np
import pytest

from openvector_ref import fit, plan, synth

TH = np.array([2 * np.pi * k / 64 for k in range(64)])
OPTS = fit.FitOptions(bootstrap_reps=100, seed=7)


def _run(user, seed, reps=7, opts=OPTS, plan_filter=None):
    p = plan.generate_plan(seed, reps=reps)
    if plan_filter:
        p = [t for t in p if plan_filter(t)]
    recs, _ = synth.generate_session(user, p, seed=seed + 100, include_path=False)
    return fit.fit_records(recs, opts)


def _arr(res, key):
    return np.array([b[key] for b in res["bins"]])


def test_identity_user_stays_near_one():
    for seed in (1, 2, 3):
        res = _run(synth.user_identity(), seed)
        assert np.max(np.abs(_arr(res, "gain") - 1.0)) < 0.015
        assert res["order"] <= 2


@pytest.mark.parametrize("make", [synth.user_up_undershoot, synth.user_diagonal, synth.user_left_right, synth.user_rich])
def test_injected_bias_recovered_within_ci(make):
    user = make()
    covers, errs = [], []
    for seed in (1, 2, 3):
        res = _run(user, seed)
        g, lo, hi = _arr(res, "gain"), _arr(res, "ci_low"), _arr(res, "ci_high")
        gt = user.true_gain(TH)
        covers.append(np.mean((gt >= lo) & (gt <= hi)))
        errs.append(np.max(np.abs(g - gt)))
    assert np.mean(covers) >= 0.75
    assert np.mean(errs) < 0.035


def test_up_undershoot_direction_and_size():
    res = _run(synth.user_up_undershoot(), 4)
    g = _arr(res, "gain")
    assert g[48] > 1.06 and abs(g[16] - 1.0) < 0.02  # up (3pi/2) ~ 1/0.92 = 1.087, down ~ 1.0


def test_bounds_respected():
    big = synth.SyntheticUser(bias_coeffs=(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.3), sigma_along=0.08)
    res = _run(big, 5, reps=4)
    g, lo, hi = _arr(res, "gain"), _arr(res, "ci_low"), _arr(res, "ci_high")
    for a in (g, lo, hi):
        assert a.min() >= 0.85 - 1e-12 and a.max() <= 1.15 + 1e-12
    assert np.isclose(g.max(), 1.15) and g.min() < 0.86  # bias of +-30 % saturates the +-15 % bounds


def test_sparse_strata_shrink_to_identity_and_neighbours():
    user = synth.SyntheticUser(bias_coeffs=(-0.06,))  # +6.4 % everywhere
    # only trials in the right half (sectors 24..31, 0..8): the left half has no data
    res = _run(user, 6, reps=7, plan_filter=lambda t: t["sector"] <= 8 or t["sector"] >= 24)
    g = _arr(res, "gain")
    assert abs(g[0] - 1 / 0.94) < 0.02           # data-rich: recovered
    assert abs(g[32] - 1.0) < 0.02               # no data at all: pulled to 1.0
    assert set(range(11, 22)) <= set(res["sparse_strata"])


def test_sparse_stratum_with_dense_neighbours_takes_their_value():
    user = synth.SyntheticUser(bias_coeffs=(-0.06,))
    res = _run(user, 7, reps=7, plan_filter=lambda t: t["sector"] != 16)  # one empty stratum (left)
    g = _arr(res, "gain")
    assert 16 in res["sparse_strata"] and abs(g[32] - 1 / 0.94) < 0.02


def test_projection_samples():
    res = _run(synth.user_left_right(), 8)
    th, g, w = fit.sample_for_projection(res["coeffs"], res["bins"], 360, 0.85, 1.15)
    assert len(th) == len(g) == len(w) == 360
    assert np.isclose(w.mean(), 1.0) and w.min() > 0
    assert g[0] < 1.0 < g[180]  # right overshoots -> lower gain
