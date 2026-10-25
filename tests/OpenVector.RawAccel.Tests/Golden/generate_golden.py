"""Generates golden vectors for OpenVector.RawAccel tests (dev-only, not shipped).

Uses the verified reference port docs/research/scripts/fit_rawaccel.py (modify, closed_form, fit,
periodic_interp). by_component() below is an extra line-by-line port of the separate-mode branch of
common/rawaccel.hpp modifier::modify (Raw Accel v1.7.1, commit 53a7213) for a constant-sensitivity
LUT, because fit_rawaccel.py only covers whole mode.

Run:  python3 -I tests/OpenVector.RawAccel.Tests/Golden/generate_golden.py
Writes tests/OpenVector.RawAccel.Tests/Golden/golden.json next to this script.
"""
import json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "..", "docs", "research", "scripts"))
import numpy as np
import fit_rawaccel as fr

def by_component(v, p):
    x, y = v
    a = np.deg2rad(p.get("alpha_deg", 0.0))
    if a != 0:
        x, y = x*np.cos(a) - y*np.sin(a), x*np.sin(a) + y*np.cos(a)
    sx = 1 + (p["cx"]-1)*p["rx"]
    sy = 1 + (p["cy"]-1)*p["ry"]
    x, y = x*sx, y*sy
    x *= p["D"]; y *= p["D"]*p["rho"]
    if p["L"] != 1 and x < 0: x *= p["L"]
    if p["U"] != 1 and y < 0: y *= p["U"]
    return [float(x), float(y)]

rng = np.random.default_rng(20261025)
C_VALUES = [2.0, 1.5, 2.25, 3.0, 0.75]   # exactly representable as float32 (LUT data is float)

modify_cases = []
for i in range(40):
    p = dict(alpha_deg=float(rng.choice([0.0, 0.0, 3.5, -7.25])),
             c=float(rng.choice(C_VALUES)),
             rx=float(rng.uniform(0, 1.5)), ry=float(rng.uniform(0, 1.5)),
             D=float(rng.uniform(0.5, 2.0)), rho=float(rng.uniform(0.7, 1.4)),
             L=float(rng.uniform(0.7, 1.4)), U=float(rng.uniform(0.7, 1.4)))
    if i % 7 == 0: p["ry"] = p["rx"]
    if i % 5 == 0: p["L"] = 1.0
    if i % 6 == 0: p["U"] = 1.0
    vs = [[float(rng.integers(-40, 41)), float(rng.integers(-40, 41))] for _ in range(6)] + [[0.0, 5.0], [-3.0, 0.0], [1.0, 1.0]]
    for v in vs:
        if v == [0.0, 0.0]: continue
        out = fr.modify(v, p)
        modify_cases.append(dict(p=p, v=v, out=[float(out[0]), float(out[1])]))

bycomp_cases = []
for i in range(20):
    p = dict(alpha_deg=float(rng.choice([0.0, 5.0])), cx=float(rng.choice(C_VALUES)), cy=float(rng.choice(C_VALUES)),
             rx=float(rng.uniform(0, 1.5)), ry=float(rng.uniform(0, 1.5)), D=float(rng.uniform(0.5, 2.0)),
             rho=float(rng.uniform(0.7, 1.4)), L=float(rng.uniform(0.7, 1.4)), U=float(rng.uniform(0.7, 1.4)))
    for _ in range(4):
        v = [float(rng.integers(-40, 41)), float(rng.integers(-40, 41))]
        if v == [0.0, 0.0]: continue
        bycomp_cases.append(dict(p=p, v=v, out=by_component(v, p)))

closed_cases = []
for i in range(10):
    p = dict(c=2.0, rx=float(rng.uniform(0, 1.5)), ry=float(rng.uniform(0, 1.5)), D=float(rng.uniform(0.5, 2)),
             rho=float(rng.uniform(0.7, 1.4)), L=float(rng.uniform(0.7, 1.4)), U=float(rng.uniform(0.7, 1.4)))
    th = np.linspace(0, 2*np.pi, 16, endpoint=False) + 0.01
    g, psi = fr.closed_form(th, p)
    closed_cases.append(dict(p=p, theta=th.tolist(), gain=g.tolist(), psi=psi.tolist()))

def fit_case(name, target):
    out = dict(name=name, target=target.tolist(), fits=[])
    for lam in (0.0, 1.0):
        q, rms, mx, dmax = fr.fit(target, lam_dir=lam)
        out["fits"].append(dict(lam=lam, params=np.exp(q).tolist(), rms=float(rms), max=float(mx), max_dir_deg=float(dmax)))
    return out

TH = fr.TH
fits = [
    fit_case("issue351_axis", fr.periodic_interp([0, 90, 180, 270], [1.01, 0.92, 1.10, 0.97])),
    fit_case("eight_dir", fr.periodic_interp(np.arange(0, 360, 45), [1.00, 0.95, 0.90, 1.04, 1.08, 1.00, 0.97, 1.06])),
    fit_case("pure_sin2", np.exp(0.08*np.sin(2*TH))),
]
for k in range(3):
    a1, b1, a2 = rng.normal(0, 0.04, 3)
    fits.append(fit_case(f"a1b1a2_{k}", np.exp(a1*np.cos(TH) + b1*np.sin(TH) + a2*np.cos(2*TH))))

doc = dict(source="generate_golden.py via docs/research/scripts/fit_rawaccel.py; Raw Accel v1.7.1 53a7213",
           theta_count=len(TH), modify=modify_cases, by_component=bycomp_cases, closed_form=closed_cases, fits=fits)
with open(os.path.join(HERE, "golden.json"), "w") as f:
    json.dump(doc, f, indent=1)
print("modify", len(modify_cases), "bycomp", len(bycomp_cases), "fits", len(fits))
for fc in fits:
    for ft in fc["fits"]:
        print(fc["name"], ft["lam"], "rms=%.4f%% max=%.3f%% dir=%.2f" % (ft["rms"]*100, ft["max"]*100, ft["max_dir_deg"]), np.round(ft["params"], 4))
