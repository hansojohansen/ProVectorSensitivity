#!/usr/bin/env python3
"""OpenVector RESEARCH build: build the "Angular gain table (RESEARCH ONLY)" for a Raw Accel
research-fork settings.json. Standard library only (Python 3.8+).

RESEARCH ONLY. The table only works with the test-signed research driver in this folder.
The official Raw Accel ignores the key, and its writer refuses to talk to the research driver.

Conventions (OpenVector D14, same as the driver):
  theta = atan2(dy, dx) of raw mouse counts; 0 = right, 90 deg = down, 180 = left, 270 = up.
  gain  = |output| / |input| relative to the rest of the profile; 1.0 = no change.
  speed = Raw Accel input speed, counts/ms (times 1000/DPI if the device DPI is set in the file).

Sources (pick one):
  --constant G                    same gain in every direction (sanity check)
  --fourier "a0=1,a1=0.03,b2=-0.05"   g = a0 + sum_k a_k cos(k theta) + b_k sin(k theta)
  --csv FILE                      columns theta_deg,gain[,speed]; '#' lines and a header row
                                  are skipped. With a speed column, every distinct speed is one
                                  speed row; they must be log-spaced (at most 16).
  --samples-json FILE             {"theta": [rad...], "gain": [...], "weight": [...]} (D14 backend
                                  input; weight is ignored here). Optional "speed": [...].

Output:
  (default)                       print the JSON fragment
  --base settings.json --out new.json [--profile NAME]
                                  copy settings.json with the table put into that profile
                                  (default: the first profile). --off removes the table instead.

Examples:
  python3 make_angular_table.py --fourier "a0=1,b2=0.06" --angles 64 \\
      --base settings.json --out settings-C-full-map.json
  python3 make_angular_table.py --off --base settings-C-full-map.json --out settings-A-identity.json
  python3 make_angular_table.py --selftest
"""

import argparse
import json
import math
import sys

KEY = "Angular gain table (RESEARCH ONLY)"
MAX_ANGLES = 128
MAX_SPEEDS = 16
GAIN_MIN = 0.5
GAIN_MAX = 2.0
TWO_PI = 2 * math.pi


def periodic_resample(thetas, gains, n):
    """Periodic linear interpolation of (theta [rad], gain) samples onto theta_i = 2 pi i / n."""
    pts = sorted(((t % TWO_PI), g) for t, g in zip(thetas, gains))
    if not pts:
        raise ValueError("no samples")
    if len(pts) == 1:
        return [pts[0][1]] * n
    out = []
    for i in range(n):
        th = TWO_PI * i / n
        # find the neighbours with wrap-around
        hi = next((k for k, p in enumerate(pts) if p[0] >= th), None)
        if hi is None or hi == 0:
            a, b = pts[-1], pts[0]
            span = (b[0] + TWO_PI) - a[0]
            off = (th - a[0]) % TWO_PI
        else:
            a, b = pts[hi - 1], pts[hi]
            span = b[0] - a[0]
            off = th - a[0]
        t = 0.0 if span <= 0 else off / span
        out.append(a[1] + t * (b[1] - a[1]))
    return out


def parse_fourier(spec):
    coeffs = {"a0": 1.0}
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        name, _, val = part.partition("=")
        name = name.strip().lower()
        if not (name[:1] in "ab" and name[1:].isdigit()):
            raise ValueError("bad Fourier term %r (use a0, a1, b1, a2, b2, ...)" % name)
        if name == "b0":
            raise ValueError("b0 has no meaning")
        coeffs[name] = float(val)
    return coeffs


def eval_fourier(coeffs, th):
    g = coeffs.get("a0", 1.0)
    for name, c in coeffs.items():
        k = int(name[1:])
        if k == 0:
            continue
        g += c * (math.cos(k * th) if name[0] == "a" else math.sin(k * th))
    return g


def speed_grid(speeds):
    speeds = sorted(set(speeds))
    if len(speeds) > MAX_SPEEDS:
        raise ValueError("at most %d distinct speeds" % MAX_SPEEDS)
    if any(s <= 0 for s in speeds):
        raise ValueError("speeds must be positive")
    if len(speeds) > 2:
        ratio = math.log(speeds[-1] / speeds[0]) / (len(speeds) - 1)
        for j, s in enumerate(speeds):
            expect = speeds[0] * math.exp(ratio * j)
            if abs(s / expect - 1) > 1e-4:
                raise ValueError("speeds must be log-spaced: got %g, expected %g at row %d" % (s, expect, j))
    return speeds


def build_table(args):
    n = args.angles
    if not 1 <= n <= MAX_ANGLES:
        raise ValueError("--angles must be 1..%d" % MAX_ANGLES)

    rows = {}  # speed (or None) -> (thetas, gains)
    if args.constant is not None:
        rows[None] = ([0.0], [args.constant])
    elif args.fourier is not None:
        c = parse_fourier(args.fourier)
        th = [TWO_PI * i / n for i in range(n)]
        rows[None] = (th, [eval_fourier(c, t) for t in th])
    elif args.csv is not None:
        with open(args.csv, newline="") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                cols = [c.strip() for c in line.split(",")]
                try:
                    vals = [float(c) for c in cols]
                except ValueError:
                    continue  # header
                if len(vals) not in (2, 3):
                    raise ValueError("CSV rows need theta_deg,gain[,speed]")
                key = vals[2] if len(vals) == 3 else None
                r = rows.setdefault(key, ([], []))
                r[0].append(math.radians(vals[0]))
                r[1].append(vals[1])
    elif args.samples_json is not None:
        with open(args.samples_json) as f:
            d = json.load(f)
        th, g = d["theta"], d["gain"]
        sp = d.get("speed")
        for k in range(len(th)):
            key = sp[k] if sp else None
            r = rows.setdefault(key, ([], []))
            r[0].append(th[k])
            r[1].append(g[k])
    else:
        raise ValueError("give one of --constant, --fourier, --csv, --samples-json")

    if None in rows and len(rows) > 1:
        raise ValueError("mix of rows with and without speed")
    if None in rows:
        speeds = [1.0]
        grid = [periodic_resample(*rows[None], n)]
    else:
        speeds = speed_grid(list(rows))
        grid = [periodic_resample(*rows[s], n) for s in speeds]

    gains = [g for row in grid for g in row]
    bad = [g for g in gains if not (GAIN_MIN <= g <= GAIN_MAX) or math.isnan(g)]
    if bad:
        if not args.clip:
            raise ValueError("%d gains outside [%g, %g] (e.g. %g); use --clip to clamp" % (len(bad), GAIN_MIN, GAIN_MAX, bad[0]))
        print("warning: clamping %d gains to [%g, %g]" % (len(bad), GAIN_MIN, GAIN_MAX), file=sys.stderr)
        gains = [min(max(g, GAIN_MIN), GAIN_MAX) for g in gains]

    return {
        "angles": n,
        "speeds": len(speeds),
        "speedMin": speeds[0],
        "speedMax": speeds[-1],
        "gains": [round(g, 6) for g in gains],
    }


def lookup(table, dx, dy, speed):
    """Python mirror of rawaccel::angular_gain_lookup (for checks and plots)."""
    n, m = table["angles"], table["speeds"]
    g = table["gains"]
    if dx == 0 and dy == 0:
        return 1.0
    th = math.atan2(dy, dx) % TWO_PI
    pos = th * n / TWO_PI
    i0 = int(pos)
    fa = pos - i0
    if i0 >= n:
        i0, fa = 0, 0.0
    i1 = 0 if i0 + 1 >= n else i0 + 1
    j0, fs = 0, 0.0
    if m > 1:
        try:
            u = math.log(speed / table["speedMin"]) / math.log(table["speedMax"] / table["speedMin"]) * (m - 1)
        except (ValueError, ZeroDivisionError):
            u = 0.0
        u = min(max(u, 0.0), m - 1)
        j0 = min(int(u), m - 2)
        fs = u - j0
    row = g[j0 * n:(j0 + 1) * n]
    v = row[i0] + fa * (row[i1] - row[i0])
    if m > 1:
        nxt = g[(j0 + 1) * n:(j0 + 2) * n]
        v += fs * ((nxt[i0] + fa * (nxt[i1] - nxt[i0])) - v)
    return min(max(v, GAIN_MIN), GAIN_MAX)


def apply_to_settings(path_in, path_out, profile_name, table):
    with open(path_in, encoding="utf-8-sig") as f:
        cfg = json.load(f)
    profiles = cfg.get("profiles")
    if not profiles:
        raise ValueError("%s has no profiles (is it a Raw Accel settings.json?)" % path_in)
    prof = profiles[0]
    if profile_name is not None:
        matches = [p for p in profiles if p.get("name") == profile_name]
        if not matches:
            raise ValueError("no profile named %r (have: %s)" % (profile_name, ", ".join(p.get("name", "?") for p in profiles)))
        prof = matches[0]
    if table is None:
        prof.pop(KEY, None)
    else:
        prof[KEY] = table
    with open(path_out, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)
        f.write("\n")


def selftest():
    # grid nodes reproduce the source, interpolation and wrap behave like the C++ tests
    t = {"angles": 4, "speeds": 1, "speedMin": 1, "speedMax": 1, "gains": [1.0, 1.5, 0.5, 2.0]}
    assert abs(lookup(t, 100, 100, 1) - 1.25) < 1e-12
    assert abs(lookup(t, 100, -100, 1) - 1.5) < 1e-12
    assert abs(lookup(t, 0, 100, 1) - 1.5) < 1e-12      # +y = down = 90 deg
    t2 = {"angles": 1, "speeds": 3, "speedMin": 1, "speedMax": 100, "gains": [0.8, 1.0, 1.6]}
    assert abs(lookup(t2, 1, 0, math.sqrt(10)) - 0.9) < 1e-12
    assert abs(lookup(t2, 1, 0, 1e6) - 1.6) < 1e-12
    r = periodic_resample([0.0, math.pi / 2, math.pi, 1.5 * math.pi], [1.0, 1.5, 0.5, 2.0], 8)
    assert [round(x, 9) for x in r] == [1.0, 1.25, 1.5, 1.0, 0.5, 1.25, 2.0, 1.5], r
    ns = argparse.Namespace(angles=16, constant=None, fourier="a0=1,b2=0.1", csv=None, samples_json=None, clip=False)
    tab = build_table(ns)
    assert len(tab["gains"]) == 16 and abs(tab["gains"][2] - 1.1) < 1e-6   # theta = 45 deg, sin(90) = 1
    assert speed_grid([1, 10, 100]) == [1, 10, 100]
    try:
        speed_grid([1, 2, 100])
        raise AssertionError("non log-spaced speeds accepted")
    except ValueError:
        pass
    print("make_angular_table selftest: OK")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group()
    src.add_argument("--constant", type=float)
    src.add_argument("--fourier")
    src.add_argument("--csv")
    src.add_argument("--samples-json")
    src.add_argument("--off", action="store_true", help="remove the table from the profile")
    ap.add_argument("--angles", type=int, default=64, help="angle grid size N (1..128, default 64)")
    ap.add_argument("--clip", action="store_true", help="clamp gains to [0.5, 2] instead of failing")
    ap.add_argument("--base", help="input Raw Accel settings.json")
    ap.add_argument("--out", help="output settings file (never overwrites --base in place)")
    ap.add_argument("--profile", help="profile name (default: first profile)")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)

    if a.selftest:
        selftest()
        return 0
    try:
        table = None if a.off else build_table(a)
        if a.base:
            if not a.out:
                raise ValueError("--base needs --out")
            if a.out == a.base:
                raise ValueError("--out must differ from --base (keep your original file)")
            apply_to_settings(a.base, a.out, a.profile, table)
            print("wrote %s" % a.out)
        else:
            if table is None:
                raise ValueError("--off needs --base and --out")
            print(json.dumps({KEY: table}, indent=2))
    except (ValueError, OSError, KeyError, json.JSONDecodeError) as e:
        print("error: %s" % e, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
