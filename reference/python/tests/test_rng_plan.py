import math
from collections import Counter

from openvector_ref.geometry import TWO_PI, sector_of, normalize_angle, along_across
from openvector_ref.plan import generate_plan
from openvector_ref.rng import SplitMix64


def test_splitmix64_reference_values():
    r = SplitMix64(0)
    assert r.next_u64() == 0xE220A8397B1DCDAF
    assert r.next_u64() == 0x6E789E6AA1B965F4
    assert 0.0 <= SplitMix64(1).next_double() < 1.0


def test_plan_balanced_and_deterministic():
    p = generate_plan(5, sectors=32, amplitudes=(1, 2, 3), reps=7)
    assert len(p) == 32 * 3 * 7
    cells = Counter((t["sector"], t["amplitude_index"]) for t in p)
    assert set(cells.values()) == {7} and len(cells) == 96
    for t in p:
        assert sector_of(t["theta"], 32) == t["sector"]
        assert 0 <= t["theta"] < TWO_PI
    assert p == generate_plan(5, sectors=32, amplitudes=(1, 2, 3), reps=7)
    assert p != generate_plan(6, sectors=32, amplitudes=(1, 2, 3), reps=7)
    # randomised order: sectors are not in sequence
    assert [t["sector"] for t in p[:10]] != sorted(t["sector"] for t in p[:10])


def test_conventions_d14():
    assert sector_of(0.0, 4) == 0 and sector_of(math.pi / 2, 4) == 1  # down
    assert sector_of(-0.01, 32) == 0 and normalize_angle(-math.pi / 2) == 3 * math.pi / 2  # up
    a, c = along_across((0, 0), (100, 0), (110, 5))
    assert math.isclose(a, 0.1) and math.isclose(c, 0.05)  # +across = toward +theta (down for a rightward flick)
