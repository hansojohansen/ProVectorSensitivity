"""Assemble a Profile v1 dict (schemas/profile.schema.json) from a fit result."""

SCHEMA_VERSION = "1.0.0"


def make_profile(fit_result, profile_id, created_at, mapping, sessions, parent_id=None, name=None):
    o = fit_result["options"]
    p = {
        "schema_version": SCHEMA_VERSION,
        "id": profile_id,
        "parent_id": parent_id,
        "created_at": created_at,
        "angle_convention": "atan2_dy_dx_counts_rad",
        "mapping": mapping,
        "model": {"type": "fourier", "quantity": "relative_along_bias", "link": "inverse_one_plus",
                  "order": fit_result["order"], "coeffs": fit_result["coeffs"], "cov": fit_result["cov"], "speed": None},
        "bounds": {"min_gain": o["min_gain"], "max_gain": o["max_gain"]},
        "bins": fit_result["bins"],
        "sessions": list(sessions),
        "fit": {"method": "fourier-pwls-v1", "signal": o["signal"], "n_trials": fit_result["n_trials"],
                "n_used": fit_result["n_used"], "order_selected": fit_result["order"], "lambda": o["lam"],
                "residual_sd": fit_result["residual_sd"], "strata": o["strata"],
                "min_trials_per_stratum": o["min_per_stratum"], "sparse_strata": fit_result["sparse_strata"],
                "cv_scores": fit_result["cv_scores"], "bootstrap_reps": o["bootstrap_reps"], "seed": o["seed"]},
        "projections": {},
        "validation": None,
    }
    if name:
        p["name"] = name
    return p
