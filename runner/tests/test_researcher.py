import json

import pytest

from run_analysis.cli import main

AGE_SEX = [{"name": "age", "type": "integer"}, {"name": "sex", "type": "string"}]


@pytest.fixture
def run(tmp_path, make_code, make_dataset):
    def _run(source):
        d = make_dataset("age,sex\n30,SECRET\n40,F\n", AGE_SEX)
        out = tmp_path / "result.json"
        assert main([str(make_code(source)), str(d), str(out)]) == 0
        text = out.read_text()
        return json.loads(text), text

    return _run


def test_returns_site_analysis_value(run):
    r, _ = run("def site_analysis(df): return {'n': len(df)}\n")
    assert r["status"] == "ok"
    assert r["result"] == {"n": 2}


def test_exception_text_never_reaches_result(run):
    r, text = run("def site_analysis(df): raise ValueError(df.sex[0])\n")
    assert r["code"] == "code_error"
    assert r["message"] == "site_analysis raised ValueError at analysis.py line 1"
    assert "SECRET" not in text


def test_keyerror_on_a_value(run):
    r, text = run('def site_analysis(df):\n    return df.set_index("age").loc["SECRET"]\n')
    assert r["code"] == "code_error"
    assert r["message"] == "site_analysis raised KeyError at analysis.py line 2"
    assert "SECRET" not in text


def test_syntax_error_at_import(run):
    r, _ = run("def site_analysis(df)\n")
    assert r["code"] == "code_error"
    assert r["message"] == "importing analysis.py raised SyntaxError at line 1"


def test_import_error_names_the_line(run):
    r, _ = run("x = 1\nraise RuntimeError('SECRET')\n")
    assert r["message"] == "importing analysis.py raised RuntimeError at line 2"


def test_missing_function(run):
    r, _ = run("x = 1\n")
    assert r["code"] == "code_error"
    assert r["message"] == "analysis.py has no site_analysis function"


@pytest.mark.parametrize(
    "source",
    ["import sys; sys.exit(3)\n", "def site_analysis(df): raise SystemExit(0)\n"],
)
def test_sys_exit_is_code_error(run, source):
    r, _ = run(source)
    assert r["code"] == "code_error"


def _run_with_flags(tmp_path, make_code, make_dataset, source, flags):
    d = make_dataset("age,sex\n30,SECRET\n40,F\n", AGE_SEX)
    out = tmp_path / "result.json"
    assert main([*flags, str(make_code(source)), str(d), str(out)]) == 0
    return out.read_text()


def test_debug_prints_the_full_traceback_to_stderr(tmp_path, make_code, make_dataset, capsys):
    text = _run_with_flags(
        tmp_path, make_code, make_dataset, "def site_analysis(df): raise ValueError(df.sex[0])\n", ["--debug"]
    )
    err = capsys.readouterr().err
    assert "ValueError: SECRET" in err
    assert "Traceback" in err
    # result.json is the same as without --debug: no data value.
    assert "SECRET" not in text
    assert json.loads(text)["message"] == "site_analysis raised ValueError at analysis.py line 1"


def test_debug_shows_import_errors(tmp_path, make_code, make_dataset, capsys):
    _run_with_flags(tmp_path, make_code, make_dataset, "import helpers\n", ["--debug"])
    assert "No module named 'helpers'" in capsys.readouterr().err


def test_without_debug_nothing_reaches_stderr(tmp_path, make_code, make_dataset, capsys):
    _run_with_flags(tmp_path, make_code, make_dataset, "def site_analysis(df): raise ValueError(df.sex[0])\n", [])
    assert capsys.readouterr().err == ""


def test_debug_with_wrong_argument_count_is_usage(capsys):
    assert main(["--debug", "a", "b"]) == 2
    assert "usage: run-analysis [--debug]" in capsys.readouterr().err


# Code-review fixes (PR #15)


def test_dataclass_with_postponed_annotations_works(run):
    r, _ = run(
        "from __future__ import annotations\n"
        "from dataclasses import dataclass\n\n"
        "@dataclass\n"
        "class Summary:\n"
        "    n: int\n\n"
        "def site_analysis(df):\n"
        "    return {'n': Summary(len(df)).n}\n"
    )
    assert r["status"] == "ok", r


def test_relative_code_path_keeps_the_line_number(tmp_path, make_code, make_dataset, monkeypatch):
    d = make_dataset("age,sex\n30,SECRET\n40,F\n", AGE_SEX)
    make_code("def site_analysis(df):\n    raise KeyError('k')\n")
    monkeypatch.chdir(tmp_path)
    assert main(["analysis.py", str(d), "result.json"]) == 0
    r = json.loads((tmp_path / "result.json").read_text())
    assert r["message"] == "site_analysis raised KeyError at analysis.py line 2"


def test_relative_code_path_syntax_error_keeps_the_line_number(tmp_path, make_code, make_dataset, monkeypatch):
    d = make_dataset("age,sex\n30,SECRET\n40,F\n", AGE_SEX)
    make_code("x = 1\ndef site_analysis(df)\n")
    monkeypatch.chdir(tmp_path)
    main(["analysis.py", str(d), "result.json"])
    r = json.loads((tmp_path / "result.json").read_text())
    assert r["message"] == "importing analysis.py raised SyntaxError at line 2"


def test_module_is_not_left_in_sys_modules(run):
    import sys

    run("def site_analysis(df): return {'n': 1}\n")
    assert "analysis" not in sys.modules
