import json

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
