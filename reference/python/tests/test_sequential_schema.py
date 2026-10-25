import json
import os

import numpy as np
import pytest

from openvector_ref import fit, plan, sequential, synth

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))


def test_reversal_effect_and_reaction_time_per_direction():
    user = synth.user_diagonal(reversal_bias=-0.04, reaction_up_extra_s=0.03)
    recs, _ = synth.generate_session(user, plan.generate_plan(3, reps=7), seed=4, include_path=False)
    res = fit.fit_records(recs, fit.FitOptions(bootstrap_reps=0))
    seq = sequential.sequential_effects(recs, coeffs=res["coeffs"])
    d = seq["reversal_minus_same"]
    assert abs(d["diff"] - (-0.04)) < 3 * d["se"]
    rt = [s["mean_us"] for s in seq["reaction_time"]]
    assert rt[6] - rt[2] > 20_000  # up (sector 6 of 8) slower than down (sector 2)


def test_no_sequential_effect_for_plain_user():
    recs, _ = synth.generate_session(synth.user_rich(), plan.generate_plan(9, reps=7), seed=10, include_path=False)
    res = fit.fit_records(recs, fit.FitOptions(bootstrap_reps=0))
    d = sequential.sequential_effects(recs, coeffs=res["coeffs"])["reversal_minus_same"]
    assert abs(d["diff"]) < 3 * d["se"]


jsonschema = pytest.importorskip("jsonschema")


@pytest.mark.parametrize("name", ["movement-trial", "profile", "session"])
def test_examples_validate(name):
    schema = json.load(open(os.path.join(ROOT, "schemas", f"{name}.schema.json")))
    jsonschema.Draft202012Validator.check_schema(schema)
    ex = json.load(open(os.path.join(ROOT, "schemas", "examples", name, "example.json")))
    jsonschema.Draft202012Validator(schema, format_checker=jsonschema.FormatChecker()).validate(ex)


def test_generated_trials_validate():
    schema = json.load(open(os.path.join(ROOT, "schemas", "movement-trial.schema.json")))
    v = jsonschema.Draft202012Validator(schema)
    recs, _ = synth.generate_session(synth.user_rich(polling_hz=8000.0), plan.generate_plan(1, reps=1)[:10], seed=1)
    for r in recs:
        v.validate(r)


def test_golden_vectors_are_current():
    """gen_golden.py output must be reproducible (re-run it after changing the reference)."""
    import gen_golden
    from openvector_ref.fit import FitOptions
    case, _, _ = gen_golden.fit_case("sparse_up", synth.user_up_undershoot(), 21, 3, 22,
                                     FitOptions(bootstrap_reps=200, seed=2024))
    golden = json.load(open(os.path.join(ROOT, "tests", "OpenVector.Core.Tests", "Golden", "fit_sparse.json")))
    a, b = case["expected"]["fit"], golden["expected"]["fit"]
    assert a["order"] == b["order"]
    np.testing.assert_allclose(a["coeffs"], b["coeffs"], rtol=0, atol=1e-12)
    np.testing.assert_allclose([x["ci_low"] for x in a["bins"]], [x["ci_low"] for x in b["bins"]], atol=1e-12)
