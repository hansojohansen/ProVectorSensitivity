"""Scratch verification of Raw Accel directional expressiveness (not product code).

Ports common/rawaccel.hpp modifier::modify (whole mode, LUT with constant
sensitivity c, no smoothing, no speed clamp) and fits target g(theta).
Screen convention: +x right, +y down (Windows mouse), theta = atan2(y, x).
"""
import numpy as np
from scipy.optimize import least_squares

def modify(v, p):
    # p: dict alpha_deg, c, rx, ry, D, rho, L, U, snap_deg
    x, y = v
    a = np.deg2rad(p.get("alpha_deg", 0.0))
    if a != 0:
        x, y = x*np.cos(a) - y*np.sin(a), x*np.sin(a) + y*np.cos(a)
    phi = 0.0
    if y != 0:
        phi = np.pi/2 if x == 0 else np.arctan(abs(y/x))
    w = p["rx"]
    if p["rx"] != p["ry"]:
        w += 2/np.pi*phi*(p["ry"]-p["rx"])
    k = 1 + (p["c"]-1)*w          # constant-f LUT => scale independent of speed
    x, y = x*k, y*k
    x *= p["D"]; y *= p["D"]*p["rho"]
    if p["L"] != 1 and x < 0: x *= p["L"]
    if p["U"] != 1 and y < 0: y *= p["U"]
    return np.array([x, y])

def closed_form(theta, p):
    """Gain magnitude and output direction for alpha=0."""
    c, s = np.cos(theta), np.sin(theta)
    phi = np.arctan2(np.abs(s), np.abs(c))
    k = 1 + (p["c"]-1)*(p["rx"] + 2/np.pi*phi*(p["ry"]-p["rx"]))
    Lx = np.where(c < 0, p["L"], 1.0); Uy = np.where(s < 0, p["U"], 1.0)
    ox, oy = p["D"]*k*Lx*c, p["D"]*k*p["rho"]*Uy*s
    return np.hypot(ox, oy), np.arctan2(oy, ox)

TH = np.linspace(0, 2*np.pi, 720, endpoint=False)

def check_closed_form():
    p = dict(c=2.0, rx=0.05, ry=0.0, D=0.8, rho=0.93, L=1.09, U=1.05)
    g, psi = closed_form(TH, p)
    for t, gg, pp in zip(TH[::37], g[::37], psi[::37]):
        out = modify((np.cos(t), np.sin(t)), p)
        assert abs(np.hypot(*out)-gg) < 1e-12 and abs(np.angle(np.exp(1j*(np.arctan2(out[1], out[0])-pp)))) < 1e-12
    print("closed form matches ported modify()")

# --- reduced parameterisation: q = [log G0, log kv (=k(90)/k(0)), log rho, log L, log U]
def model(q, th):
    G0, kv, rho, L, U = np.exp(q)
    c, s = np.cos(th), np.sin(th)
    phi = np.arctan2(np.abs(s), np.abs(c))
    k = 1 + (kv-1)*phi/(np.pi/2)          # normalised so k(0)=1
    Lx = np.where(c < 0, L, 1.0); Uy = np.where(s < 0, U, 1.0)
    ox, oy = G0*k*Lx*c, G0*k*rho*Uy*s
    return np.hypot(ox, oy), np.angle(np.exp(1j*(np.arctan2(oy, ox)-th)))

def fit(target, lam_dir=0.0, fix_rho=None):
    def res(q):
        if fix_rho is not None: q = np.r_[q[:2], np.log(fix_rho), q[2:]]
        g, d = model(q, TH)
        r = np.log(g) - np.log(target)
        return np.r_[r, lam_dir*d] if lam_dir else r
    q0 = np.zeros(4 if fix_rho is not None else 5); q0[0] = np.log(target.mean())
    sol = least_squares(res, q0)
    q = sol.x if fix_rho is None else np.r_[sol.x[:2], np.log(fix_rho), sol.x[2:]]
    g, d = model(q, TH)
    rel = g/target-1
    return q, np.sqrt(np.mean(rel**2)), np.max(np.abs(rel)), np.rad2deg(np.max(np.abs(d)))

def periodic_interp(angles_deg, vals):
    # smooth periodic target via low-order Fourier least squares through given points
    a = np.deg2rad(np.asarray(angles_deg)); n = len(a)
    K = (n-1)//2
    A = np.column_stack([np.ones(n)] + [f(k*a) for k in range(1, K+1) for f in (np.cos, np.sin)])
    coef = np.linalg.lstsq(A, np.log(vals), rcond=None)[0]
    At = np.column_stack([np.ones_like(TH)] + [f(k*TH) for k in range(1, K+1) for f in (np.cos, np.sin)])
    return np.exp(At @ coef)

def report(name, target):
    print(f"\n== {name}")
    for lab, kw in [("free (rho+k)", {}), ("rho fixed 1 (k only for V/H)", dict(fix_rho=1.0)),
                    ("free + direction penalty", dict(lam_dir=1.0))]:
        q, rms, mx, dmax = fit(target, **kw)
        G0, kv, rho, L, U = np.exp(q)
        print(f"  {lab:32s} rms={rms*100:5.2f}%  max={mx*100:5.2f}%  max dir err={dmax:4.2f} deg | "
              f"G0={G0:.3f} kv={kv:.3f} rho={rho:.3f} L={L:.3f} U={U:.3f}")

def harmonic_basis_test():
    # Which first-order harmonics can the family generate? Linearise log g around identity.
    eps = 1e-4; q0 = np.zeros(5)
    J = np.column_stack([(np.log(model(q0+eps*np.eye(5)[i], TH)[0]) - np.log(model(q0, TH)[0]))/eps for i in range(5)])
    print("\nFourier content of d(log g)/dq_i (|coef| of harmonics 0..6, cos/sin):")
    names = ["G0", "kv", "rho", "L", "U"]
    for i in range(5):
        f = np.fft.rfft(J[:, i])/len(TH)*2; f[0] /= 2
        print(f"  {names[i]:3s} " + " ".join(f"h{n}:{f[n].real:+.3f}c{-f[n].imag:+.3f}s" for n in range(7)))

if __name__ == "__main__":
    check_closed_form()
    harmonic_basis_test()
    # issue #351 example: right +1%, down -8%, left +10%, up -3% (only 4 axes -> exactly representable)
    report("axis-only target from issue #351 (smooth interp)", periodic_interp([0, 90, 180, 270], [1.01, 0.92, 1.10, 0.97]))
    # 8-direction target with independent diagonals
    ang8 = np.arange(0, 360, 45)
    report("8-dir target, diagonals independent", periodic_interp(ang8, [1.00, 0.95, 0.90, 1.04, 1.08, 1.00, 0.97, 1.06]))
    # oblique (sin 2theta) component: favours 45/225 over 135/315
    report("pure oblique 2nd harmonic +-8%", np.exp(0.08*np.sin(2*TH)))
    # random smooth targets, harmonics up to 4, ~5% amplitude
    rng = np.random.default_rng(1); stats = []
    for _ in range(200):
        logg = sum(0.05/n*(rng.standard_normal()*np.cos(n*TH)+rng.standard_normal()*np.sin(n*TH)) for n in range(1, 5))
        stats.append(fit(np.exp(logg))[1:])
    stats = np.array(stats)
    print(f"\n== 200 random smooth targets (log-amplitude ~5%/n, n=1..4): median rms={np.median(stats[:,0])*100:.2f}% "
          f"p90 rms={np.percentile(stats[:,0],90)*100:.2f}%  median max={np.median(stats[:,1])*100:.2f}%  median dir err={np.median(stats[:,2]):.2f} deg")
