import hashlib

import pytest

from run_analysis.check import MAX_CODE_BYTES, check_code, main
from run_analysis.errors import AnalysisError

GOOD = (
    "def site_analysis(df):\n"
    "    return {'n': len(df)}\n"
    "\n"
    "\n"
    "def aggregate(results):\n"
    "    return {'n': sum(r['n'] for r in results)}\n"
)
TEMPLATE_BODY = (
    "def site_analysis(df):\n"
    '    """doc"""\n'
    '    raise NotImplementedError("write your analysis here")\n'
    "\n"
    "\n"
    "def aggregate(results):\n"
    "    return {'n': 0}\n"
)


@pytest.mark.parametrize("source", [GOOD.encode(), b"\xef\xbb\xbf" + GOOD.encode()], ids=["plain", "bom"])
def test_check_code_accepts(source):
    check_code(source)


@pytest.mark.parametrize(
    "source, message_part",
    [
        (GOOD.replace("def site_analysis", "def other").encode(), "no site_analysis(df) function"),
        (GOOD.replace("def aggregate", "def other").encode(), "no aggregate(results) function"),
        (GOOD.replace("def aggregate(results):", "class C:\n  def aggregate(results):").encode(),
         "no aggregate(results) function"),
        (GOOD.replace("def aggregate", "async def aggregate").encode(), "no aggregate(results) function"),
        (GOOD.split("def aggregate")[0].encode() + b"aggregate = lambda results: {'n': 0}\n",
         "no aggregate(results) function"),
        (GOOD.encode() + b"#" * MAX_CODE_BYTES, f"at most {MAX_CODE_BYTES} bytes"),
        (b"def site_analysis(df:\n", "line 1"),
        (b"\xff\xfe", "UTF-8"),
        (TEMPLATE_BODY.encode(), "site_analysis is still the template"),
    ],
    ids=["no-site", "no-aggregate", "nested", "async", "lambda", "too-large", "syntax", "not-utf8", "template"],
)
def test_check_code_refuses(source, message_part):
    with pytest.raises(AnalysisError) as e:
        check_code(source)
    assert e.value.code == "invalid_code"
    assert message_part in e.value.message


SITE_OK = "    return {'n': len(df), 'mean_age': float(df['age'].mean())}\n"
AGG_OK = "    return {'n': sum(r['n'] for r in results)}\n"
AGES = "age\n30\n40\n50\n"
AGE_SCHEMA = [{"name": "age", "type": "integer"}]


def analysis(site_body=SITE_OK, agg_body=AGG_OK, prelude=""):
    return f"{prelude}def site_analysis(df):\n{site_body}\n\ndef aggregate(results):\n{agg_body}"


def test_passes_with_dry_run(make_code, make_dataset, capsys):
    code = make_code(analysis())
    assert main(["--dataset", str(make_dataset(AGES, AGE_SCHEMA)), str(code)]) == 0
    assert capsys.readouterr().out.splitlines() == [
        "analysis.py passed (dry run on dataset)",
        "code_sha256 " + hashlib.sha256(code.read_bytes()).hexdigest(),
    ]


def test_passes_without_dataset(make_code, capsys):
    assert main([str(make_code(analysis()))]) == 0
    assert capsys.readouterr().out.splitlines()[0] == "analysis.py passed (no dry run: no synthetic data)"


@pytest.mark.parametrize(
    "site_body, agg_body, expected",
    [
        ("    raise KeyError('age')\n", AGG_OK, "dry run on dataset: site_analysis raised KeyError at analysis.py line"),
        (SITE_OK, "    raise ZeroDivisionError\n", "dry run on dataset: aggregate raised ZeroDivisionError at analysis.py line"),
        (SITE_OK, "    return [1]\n", "dry run on dataset: aggregate must return a dict, not list"),
        ("    import time\n    time.sleep(30)\n", AGG_OK, "dry run on dataset: site_analysis did not finish within 1 seconds"),
    ],
    ids=["site-raises", "aggregate-raises", "aggregate-bad-result", "timeout"],
)
def test_dry_run_blocks(make_code, make_dataset, capsys, site_body, agg_body, expected):
    timeout = ["--timeout", "1"] if "sleep" in site_body else []
    code = make_code(analysis(site_body, agg_body))
    assert main([*timeout, "--dataset", str(make_dataset(AGES, AGE_SCHEMA)), str(code)]) == 1
    out = capsys.readouterr().out.splitlines()
    assert len(out) == 1 and out[0].startswith(expected)


def test_broken_synthetic_data_skips(make_code, make_dataset, capsys):
    broken = make_dataset("height\n1\n", AGE_SCHEMA)
    assert main(["--dataset", str(broken), str(make_code(analysis()))]) == 0
    assert capsys.readouterr().out.splitlines()[0].startswith(
        "analysis.py passed (no dry run: the synthetic data could not be used: ")


def test_invalid_code_blocks_before_dry_run(make_code, tmp_path, capsys):
    # The dataset directory doesn't exist: had a dry run started, the message would be about the data.
    code = make_code("def site_analysis(df):\n    return {'n': 0}\n")
    assert main(["--dataset", str(tmp_path / "nowhere"), str(code)]) == 1
    assert "no aggregate(results) function" in capsys.readouterr().out


def test_dry_run_output_is_only_ours(make_code, make_dataset, capsys):
    noisy = "import sys\nprint('x\\n' * 1000)\nprint('oops', file=sys.stderr)\n"
    assert main(["--dataset", str(make_dataset(AGES, AGE_SCHEMA)), str(make_code(analysis(prelude=noisy)))]) == 0
    captured = capsys.readouterr()
    assert len(captured.out.splitlines()) == 2 and captured.err == ""


def test_usage(capsys):
    assert main([]) == 2
    assert "usage: check-analysis" in capsys.readouterr().err
