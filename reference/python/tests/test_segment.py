import math

import numpy as np

from openvector_ref.segment import segment
from openvector_ref.synth import _min_jerk, _sample_packets


def _packets(subs, t_click=0.6, hz=1000.0):
    t, dx, dy, _ = _sample_packets(subs, t_click, hz)
    return t.tolist(), dx.tolist(), dy.tolist()


def test_single_flick_has_no_corrections():
    t, dx, dy = _packets([(0.2, 0.25, 800.0, -300.0)])
    r = segment(t, dx, dy, t_ref=0, direction=math.atan2(-300, 800))
    assert r.n_corrections == 0
    assert math.hypot(r.primary_end[0] - 800, r.primary_end[1] + 300) < 0.01 * 854
    assert abs(r.t_onset_us - 200_000) < 40_000 and abs(r.t_peak_us - 325_000) < 5_000


def test_primary_plus_correction_after_pause():
    t, dx, dy = _packets([(0.2, 0.25, 900.0, 0.0), (0.5, 0.1, 80.0, 10.0)], t_click=0.7)
    r = segment(t, dx, dy, t_ref=0, direction=0.0)
    assert r.n_corrections == 1
    assert abs(r.primary_end[0] - 900) < 9 and abs(r.final_end[0] - 980) < 9


def test_overlapping_correction_split_at_dip():
    t, dx, dy = _packets([(0.2, 0.25, 900.0, 0.0), (0.43, 0.12, 120.0, 0.0)], t_click=0.7)
    r = segment(t, dx, dy, t_ref=0, direction=0.0)
    assert r.n_corrections == 1
    assert 850 < r.primary_end[0] < 920


def test_overshoot_reversal_is_a_correction():
    t, dx, dy = _packets([(0.2, 0.25, 1000.0, 0.0), (0.47, 0.12, -100.0, 0.0)], t_click=0.7)
    r = segment(t, dx, dy, t_ref=0, direction=0.0)
    assert r.n_corrections == 1 and r.submovements[1].dx < 0


def test_low_polling_and_batched_timestamps():
    t, dx, dy = _packets([(0.2, 0.25, 800.0, 0.0)], hz=125.0)
    r = segment(t, dx, dy, t_ref=0, direction=0.0)
    assert r.n_corrections == 0 and abs(r.primary_end[0] - 800) < 12
    # duplicate (batch) timestamps are merged, not treated as zero-length intervals
    t2 = [ti - (ti % 2000) for ti in t]
    r2 = segment(t2, dx, dy, t_ref=0, direction=0.0)
    assert r2 is not None and abs(r2.primary_end[0] - 800) < 20


def test_no_movement():
    assert segment([], [], []) is None
    assert segment([1000], [0], [0]) is None
