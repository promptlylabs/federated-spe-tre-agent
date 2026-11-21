import json

import numpy
import pytest

from run_analysis.cli import main
from run_analysis.errors import AnalysisError
from run_analysis.result import MAX_RESULT_BYTES, check_result

OVERHEAD = len('{"n":1,"x":""}')


@pytest.mark.parametrize(
    "value",
    [
        [1, 2],
        {"n": 1, "x": object()},
        {"n": 1, "x": float("nan")},
        {"n": 1, "x": [numpy.float64("inf")]},
        {"mean": 1},
        {"n": -1},
        {"n": True},
        {"n": 1.5},
        {1: 2, "n": 1},
        {"n": 1, "x": {2: 3}},
    ],
    ids=repr,
)
def test_invalid_return(value):
    with pytest.raises(AnalysisError) as e:
        check_result(value)
    assert e.value.code == "invalid_result"


def test_not_a_dict_message():
    with pytest.raises(AnalysisError) as e:
        check_result([1])
    assert e.value.message == "site_analysis must return a dict, not list"


def test_nan_message():
    with pytest.raises(AnalysisError) as e:
        check_result({"n": 1, "x": float("nan")})
    assert e.value.message == "the returned dict is not JSON-serialisable (NaN or infinity)"


def test_self_reference_is_invalid_result():
    value = {"n": 1}
    value["self"] = value
    with pytest.raises(AnalysisError) as e:
        check_result(value)
    assert e.value.code == "invalid_result"


def test_result_at_limit_is_accepted():
    value = {"n": 1, "x": "a" * (MAX_RESULT_BYTES - OVERHEAD)}
    assert check_result(value) == (value, 1)


def test_result_over_limit_is_result_too_large():
    with pytest.raises(AnalysisError) as e:
        check_result({"n": 1, "x": "a" * (MAX_RESULT_BYTES - OVERHEAD + 1)})
    assert e.value.code == "result_too_large"
    assert e.value.message == "the returned dict is 61441 bytes; the limit is 61440"


def test_numpy_scalars_are_accepted():
    result, n = check_result({"n": numpy.int64(2), "mean": numpy.float64(1.5), "v": numpy.array([1, 2])})
    assert n == 2
    assert result == {"n": 2, "mean": 1.5, "v": [1, 2]}
    assert type(result["n"]) is int


def test_non_ascii_counts_utf8_bytes():
    k = (MAX_RESULT_BYTES - OVERHEAD) // 2
    check_result({"n": 1, "x": "é" * k})
    with pytest.raises(AnalysisError) as e:
        check_result({"n": 1, "x": "é" * (k + 1)})
    assert e.value.code == "result_too_large"


def test_ok_result_has_n_from_the_dict(tmp_path, make_code, make_dataset):
    d = make_dataset("age\n1\n2\n", [{"name": "age", "type": "integer"}])
    out = tmp_path / "result.json"
    assert main([str(make_code("def site_analysis(df): return {'n': 7}\n")), str(d), str(out)]) == 0
    r = json.loads(out.read_text())
    assert r["status"] == "ok"
    assert r["n"] == 7
