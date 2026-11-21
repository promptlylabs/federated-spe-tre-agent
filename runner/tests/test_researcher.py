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
