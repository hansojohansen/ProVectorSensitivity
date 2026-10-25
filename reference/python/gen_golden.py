"""Regenerate golden vectors for the C# tests and the schema example files.

    cd reference/python && python3 gen_golden.py

Outputs (overwritten):
    tests/OpenVector.Core.Tests/Golden/*.json
    schemas/examples/<schema>/example.json
"""
import json
import os
from dataclasses import asdict

from openvector_ref import fit, metrics, plan, profile, segment, sequential, synth

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
GOLDEN = os.path.join(ROOT, "tests", "OpenVector.Core.Tests", "Golden")
EXAMPLES = os.path.join(ROOT, "schemas", "examples")
MAPPING = {"dpi": 800, "yaw_deg_per_count": 0.022, "pitch_deg_per_count": 0.022, "cm_per_360": 51.95, "fov_h_deg": 103.0}


def dump(path, obj, indent=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        json.dump(obj, f, indent=indent, allow_nan=False)
        f.write("\n")


def strip(rec):
    r = dict(rec)
    r.pop("path", None)
    r["kinematics"] = {k: v for k, v in r["kinematics"].items() if k != "submovements"}
    return r


def fit_case(name, user, plan_seed, reps, sim_seed, opts):
    p = plan.generate_plan(plan_seed, reps=reps)
    recs, _ = synth.generate_session(user, p, seed=sim_seed, include_path=False, session_id=f"golden-{name}")
    res = fit.fit_records(recs, opts)
    th, g, w = fit.sample_for_projection(res["coeffs"], res["bins"], 360, opts.min_gain, opts.max_gain)
    ms = [metrics.trial_metrics(r) for r in recs]
    return {
        "description": f"Synthetic user '{name}' (bias coeffs {list(user.bias_coeffs)}), plan seed {plan_seed}, reps {reps}.",
        "true_bias_coeffs": list(user.bias_coeffs),
        "options": asdict(opts),
        "trials": [strip(r) for r in recs],
        "expected": {
            "fit": {k: res[k] for k in ("order", "coeffs", "cov", "cv_scores", "residual_sd", "n_trials", "n_used",
                                        "sparse_strata", "bins")},
            "projection_samples": {"n": 360, "theta": th.tolist(), "gain": g.tolist(), "weight": w.tolist()},
            "metrics_first20": ms[:20],
            "throughput": metrics.throughput(ms),
            "sequential": sequential.sequential_effects(recs, coeffs=res["coeffs"]),
        },
    }, recs, res


def main():
    # 1. trial plan
    pl = plan.generate_plan(42, sectors=32, amplitudes=(5.0, 15.0, 30.0), reps=2)
    dump(os.path.join(GOLDEN, "plan.json"), {"seed": 42, "sectors": 32, "amplitudes": [5.0, 15.0, 30.0], "reps": 2,
                                             "trials": pl}, indent=1)

    # 2. segmentation on raw packet streams (incl. an 8 kHz and a 125 Hz user)
    seg_cases = []
    for poll, seed in ((1000.0, 7), (8000.0, 8), (125.0, 9)):
        u = synth.user_rich(polling_hz=poll, blend_prob=0.3)
        recs, truths = synth.generate_session(u, plan.generate_plan(seed, reps=1)[:8], seed=seed,
                                              session_id=f"seg-{int(poll)}")
        for r, t in zip(recs, truths):
            pth = r["path"]
            click = r["timing"]["t_final_click_us"]
            keep = [i for i, tt in enumerate(pth["t_us"]) if tt <= click]
            sres = segment.segment([pth["t_us"][i] for i in keep], [pth["dx"][i] for i in keep],
                                   [pth["dy"][i] for i in keep], t_ref=r["timing"]["t_stimulus_us"],
                                   direction=r["geometry"]["direction_theta"])
            seg_cases.append({"polling_hz": poll, "t_ref_us": r["timing"]["t_stimulus_us"],
                              "t_end_us": click, "direction": r["geometry"]["direction_theta"],
                              "path": pth, "truth": t, "expected": None if sres is None else {
                                  "t_onset_us": sres.t_onset_us, "t_peak_us": sres.t_peak_us,
                                  "t_primary_end_us": sres.t_primary_end_us, "t_end_us": sres.t_end_us,
                                  "peak_speed": sres.peak_speed, "primary_end": list(sres.primary_end),
                                  "final_end": list(sres.final_end), "path_length": sres.path_length,
                                  "submovements": [asdict(s) for s in sres.submovements]}})
    dump(os.path.join(GOLDEN, "segmentation.json"),
         {"options": asdict(segment.SegmentationOptions()), "cases": seg_cases})

    # 3. full fits: a dense rich user (768 trials) and a sparse one (288 trials, every stratum sparse)
    opts = fit.FitOptions(bootstrap_reps=200, seed=2024)
    rich, rich_recs, rich_res = fit_case("rich", synth.user_rich(reversal_bias=-0.03, reaction_up_extra_s=0.02),
                                         11, 8, 12, opts)
    dump(os.path.join(GOLDEN, "fit_rich.json"), rich)
    sparse, _, _ = fit_case("sparse_up", synth.user_up_undershoot(), 21, 3, 22, opts)
    dump(os.path.join(GOLDEN, "fit_sparse.json"), sparse)

    # 4. schema examples
    dump(os.path.join(EXAMPLES, "movement-trial", "example.json"), _example_trial(), indent=2)
    prof = profile.make_profile(rich_res, "3f1c2a9e-5b7d-4c1e-9a0b-8d6f4e2c1a00", "2026-10-25T12:00:00+00:00", MAPPING,
                                ["golden-rich"], name="Golden rich user")
    prof["projections"] = {"rawaccel-1.7": {"params": {"note": "opaque, owned by OpenVector.RawAccel"},
                                            "residual_rms": 0.025, "explained_var": 0.76, "max_tilt_deg": 3.1}}
    prof["validation"] = {"status": "none"}
    dump(os.path.join(EXAMPLES, "profile", "example.json"), prof, indent=2)
    dump(os.path.join(EXAMPLES, "session", "example.json"), _example_session(), indent=2)
    print("golden vectors written to", GOLDEN)


def _example_trial():
    recs, _ = synth.generate_session(synth.user_up_undershoot(), plan.generate_plan(3, reps=1)[:2], seed=3,
                                     session_id="example-session")
    r = recs[1]
    r["derivation"] = {"deriver": "ov-trialize-1", "session_file": "session.json"}
    r["device"].update({"vid": 0x046D, "pid": 0xC547, "name": "example mouse", "polling_hz_measured": 999.7})
    return r


def _example_session():
    return {
        "schema_version": "1.0.0", "session_id": "example-session", "kind": "calibration",
        "clock": {"type": "qpc_us", "qpc_frequency_hz": 10_000_000, "utc_offset_us": 1_793_000_000_000_000},
        "started_at": "2026-10-25T19:30:00+02:00", "ended_at": "2026-10-25T19:52:10+02:00",
        "t_start_us": 1_000_000, "t_end_us": 1_331_000_000, "duration_us": 1_330_000_000,
        "trainer": {"version": "0.1.0", "protocol_id": "ov-flick-32x3", "seed": 42,
                    "plan": {"sectors": 32, "amplitudes_deg": [5, 15, 30], "reps": 7}},
        "device": {"vid": 0x046D, "pid": 0xC547, "name": "example mouse", "dpi": 800,
                   "polling_hz_nominal": 1000, "polling_hz_measured": 999.7},
        "display": {"refresh_hz": 240, "resolution": [2560, 1440], "vrr": False, "present_mode": "flip_discard"},
        "view": {"yaw_deg_per_count": 0.022, "pitch_deg_per_count": 0.022, "fov_h_deg": 103, "fov_scaling": "hor+",
                 "cm_per_360": 51.95, "game_preset": "cs2"},
        "rawaccel": {"detected": True, "version": "1.7.1", "settings_sha256": "0" * 64},
        "active_gain_map_id": "identity",
        "breaks": [{"t_start_us": 400_000_000, "t_end_us": 430_000_000, "reason": "scheduled"}],
        "self_report": {"warmed_up": True, "fatigue_1_5": 2},
        "streams": [
            {"kind": "raw_input", "path": "raw_input.parquet", "format": "ov-raw-input", "format_schema_version": "1.0.0",
             "compression": "parquet_internal"},
            {"kind": "frames", "path": "frames.parquet", "format": "ov-frames", "format_schema_version": "1.0.0",
             "compression": "parquet_internal"},
            {"kind": "events", "path": "events.jsonl.zst", "format": "ov-events-jsonl", "format_schema_version": "1.0.0",
             "compression": "zstd"},
            {"kind": "trials", "path": "trials.jsonl", "format": "ov-movement-trial-jsonl", "format_schema_version": "1.0.0",
             "compression": "none", "records": 672},
        ],
    }


if __name__ == "__main__":
    main()
