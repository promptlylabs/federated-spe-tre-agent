import json

import pytest

from run_analysis.cli import main
from run_analysis.result import write_error, write_ok


def test_wrong_argument_count_prints_usage_and_returns_2(capsys):
    assert main(["only-one"]) == 2
    assert "usage: run-analysis" in capsys.readouterr().err


def test_missing_csv_writes_error_result_and_returns_0(tmp_path, make_code):
    out = tmp_path / "result.json"
    (tmp_path / "empty").mkdir()
    assert main([str(make_code("")), str(tmp_path / "empty"), str(out)]) == 0
    assert json.loads(out.read_text()) == {"status": "error", "code": "dataset_not_found", "message": "no .csv file in the dataset directory"}


def test_unwritable_result_path_returns_1(tmp_path, make_code, make_dataset):
    d = make_dataset("age\n1\n", [{"name": "age", "type": "integer"}])
    assert main([str(make_code("")), str(d), str(tmp_path / "missing-dir" / "result.json")]) == 1


def test_write_ok_shape(tmp_path):
    p = tmp_path / "r.json"
    write_ok(p, "ab" * 32, 3, {"mean": 1.5})
    assert json.loads(p.read_text()) == {"status": "ok", "datasetSha256": "ab" * 32, "n": 3, "result": {"mean": 1.5}}


def test_write_error_includes_sha_only_when_known(tmp_path):
    p = tmp_path / "r.json"
    write_error(p, "code_error", "m", "cd" * 32)
    assert json.loads(p.read_text())["datasetSha256"] == "cd" * 32
    write_error(p, "code_error", "m", None)
    assert "datasetSha256" not in json.loads(p.read_text())


MEAN_OF_MEANS = (
    "def aggregate(results):\n"
    "    n = sum(r['n'] for r in results)\n"
    "    return {'n': n, 'mean_age': sum(r['n'] * r['mean_age'] for r in results) / n}\n"
)
TWO_HOSPITALS = [{"n": 3, "mean_age": 40.0}, {"n": 1, "mean_age": 20.0}]


@pytest.mark.parametrize(
    "source, results_text, expected",
    [
        (MEAN_OF_MEANS, json.dumps(TWO_HOSPITALS),
         {"status": "ok", "n": 4, "result": {"n": 4, "mean_age": 35.0}}),
        ("def site_analysis(df):\n    return {'n': 0}\n", json.dumps(TWO_HOSPITALS),
         {"status": "error", "code": "code_error", "message": "analysis.py has no aggregate function"}),
        ("def aggregate(results):\n    raise ValueError(results[0]['mean_age'])\n", json.dumps(TWO_HOSPITALS),
         {"status": "error", "code": "code_error", "message": "aggregate raised ValueError at analysis.py line 2"}),
        ("def aggregate(results):\n    return [1]\n", json.dumps(TWO_HOSPITALS),
         {"status": "error", "code": "invalid_result", "message": "aggregate must return a dict, not list"}),
        ("def aggregate(results):\n    return {'n': -1}\n", json.dumps(TWO_HOSPITALS),
         {"status": "error", "code": "invalid_result", "message": "n must be a non-negative integer"}),
        (MEAN_OF_MEANS, "{}",
         {"status": "error", "code": "invalid_dataset", "message": "the hospital results could not be read"}),
        (MEAN_OF_MEANS, "not json",
         {"status": "error", "code": "invalid_dataset", "message": "the hospital results could not be read"}),
        (MEAN_OF_MEANS, "[1, 2]",
         {"status": "error", "code": "invalid_dataset", "message": "the hospital results could not be read"}),
    ],
    ids=["mean-of-means", "no-aggregate", "raises", "not-a-dict", "bad-n", "not-a-list", "not-json", "not-dicts"],
)
def test_aggregate(tmp_path, make_code, source, results_text, expected):
    results = tmp_path / "results.json"
    results.write_text(results_text)
    out = tmp_path / "result.json"

    assert main(["--aggregate", str(make_code(source)), str(results), str(out)]) == 0
    assert json.loads(out.read_text()) == expected


def test_aggregate_with_debug_prints_the_traceback(tmp_path, make_code, capsys):
    results = tmp_path / "results.json"
    results.write_text(json.dumps(TWO_HOSPITALS))
    out = tmp_path / "result.json"

    code = make_code("def aggregate(results):\n    raise ValueError('secret')\n")
    assert main(["--debug", "--aggregate", str(code), str(results), str(out)]) == 0
    assert "secret" in capsys.readouterr().err
    assert "secret" not in out.read_text()


def test_aggregate_wrong_argument_count_prints_usage(capsys):
    assert main(["--aggregate", "a.py", "results.json"]) == 2
    assert "--aggregate" in capsys.readouterr().err


def test_write_ok_without_a_dataset_has_no_sha(tmp_path):
    p = tmp_path / "r.json"
    write_ok(p, None, 4, {"n": 4})
    assert json.loads(p.read_text()) == {"status": "ok", "n": 4, "result": {"n": 4}}
