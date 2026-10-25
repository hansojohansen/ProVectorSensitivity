"""Angle conventions (D14) and along/across decomposition."""
import math

TWO_PI = 2.0 * math.pi


def normalize_angle(theta: float) -> float:
    """Map to [0, 2pi)."""
    t = math.fmod(theta, TWO_PI)
    if t < 0:
        t += TWO_PI
    if t >= TWO_PI:  # fmod rounding edge case
        t = 0.0
    return t


def direction(dx: float, dy: float) -> float:
    """theta = atan2(dy, dx) in raw-count coordinates (y down), in [0, 2pi)."""
    return normalize_angle(math.atan2(dy, dx))


def sector_of(theta: float, n: int) -> int:
    """Sector index for n sectors centred on 2*pi*k/n (sector 0 is centred on 'right')."""
    return int(math.floor(normalize_angle(theta) / TWO_PI * n + 0.5)) % n


def sector_center(k: int, n: int) -> float:
    return TWO_PI * k / n


def along_across(start, target, point):
    """Relative along/across error of `point` for the movement start->target.

    along  = (P - S).u / A - 1   (>0 overshoot, <0 undershoot)
    across = (P - S).n / A       with n = u rotated by +pi/2 in theta (i.e. (-uy, ux))
    """
    tx, ty = target[0] - start[0], target[1] - start[1]
    a = math.hypot(tx, ty)
    ux, uy = tx / a, ty / a
    px, py = point[0] - start[0], point[1] - start[1]
    along = (px * ux + py * uy) / a - 1.0
    across = (-px * uy + py * ux) / a
    return along, across
