import json

import pytest

from run_analysis.errors import AnalysisError
from run_analysis.notebook import TEMPLATE_DIR, convert, main

GOOD_FUNCS = (
    "def site_analysis(df):\n"
    "    return {'n': len(df)}\n"
    "\n"
    "\n"
    "def aggregate(results):\n"
    "    return {'n': sum(r['n'] for r in results)}\n"
)
MARKER = "# Generated from study.ipynb by convert-notebook. Edit the notebook, not this file.\n"


def code(source, tags=(), outputs=(), count=None):
    lines = source.splitlines(keepends=True)
    return {"cell_type": "code", "execution_count": count, "metadata": {"tags": list(tags)} if tags else {},
            "outputs": list(outputs), "source": lines}


def md(source):
    return {"cell_type": "markdown", "metadata": {}, "source": source.splitlines(keepends=True)}


def nb(*cells):
    return json.dumps({"nbformat": 4, "nbformat_minor": 5, "metadata": {}, "cells": list(cells)})


def test_converts_without_spe_only_outputs_markdown_and_magics():
    text = convert(nb(md("# intro"), code("import pandas as pd"), code("df.head()", tags=["spe-only"]),
                      code("%matplotlib inline\nx = 1", outputs=[{"output_type": "stream", "text": "secret"}]),
                      code("%%time\ny = 2"), code(GOOD_FUNCS)), "study.ipynb")
    assert text == MARKER + "import pandas as pd\n\n\nx = 1\n\n\ny = 2\n\n\n" + GOOD_FUNCS


def _base():
    return [code("import pandas as pd", count=1), code("x = 1\n\n", count=2), code(GOOD_FUNCS, count=3)]


def _variant(name):
    cells = _base()
    if name == "outputs-and-counts-changed":
        for i, c in enumerate(cells):
            c["execution_count"] = 40 + i
            c["outputs"] = [{"output_type": "stream", "name": "stdout", "text": [f"run {i}\n"]}]
    elif name == "source-as-string":
        for c in cells:
            c["source"] = "".join(c["source"])
    elif name == "crlf-and-trailing-spaces":
        for c in cells:
            c["source"] = [line.replace("\n", "  \r\n") for line in c["source"]]
    return cells


@pytest.mark.parametrize("variant", ["outputs-and-counts-changed", "source-as-string", "crlf-and-trailing-spaces"])
def test_same_notebook_same_bytes(variant):
    assert convert(nb(*_variant(variant)), "study.ipynb") == convert(nb(*_base()), "study.ipynb")


@pytest.mark.parametrize(
    "cell, message_part",
    [
        ("!pip install x", "cell 1, line 1"),
        ("  x = !ls", "cell 1, line 1"),
        ("%pip install x", "%pip"),
        ("%conda install x", "%conda"),
        ("%%bash\nls", "%%bash"),
        ("%%writefile f.py\nx=1", "%%writefile"),
        ("df?", "syntax error"),
    ],
    ids=["bang", "bang-assign", "pip", "conda", "bash", "unknown-cell-magic", "ipython-help"],
)
def test_refuses(cell, message_part):
    with pytest.raises(AnalysisError) as e:
        convert(nb(code(cell), code(GOOD_FUNCS)), "study.ipynb")
    assert e.value.code == "invalid_notebook"
    assert message_part in e.value.message
    assert "spe-only" in e.value.message


@pytest.mark.parametrize("text", ["not json", '{"nbformat": 3, "worksheets": []}', '{"nbformat": 4}'],
                         ids=["not-json", "nbformat-3", "no-cells"])
def test_refuses_what_is_not_a_notebook(text):
    with pytest.raises(AnalysisError) as e:
        convert(text, "study.ipynb")
    assert e.value.code == "invalid_notebook"
    assert "is not a Jupyter notebook" in e.value.message


@pytest.mark.parametrize(
    "existing, force, written",
    [(None, False, True), ("MARKER", False, True), ("TEMPLATE", False, True),
     ("x = 1\n", False, False), ("x = 1\n", True, True)],
    ids=["missing", "generated", "untouched-template", "hand-written", "hand-written-force"],
)
def test_overwrite_rule(tmp_path, capsys, existing, force, written):
    notebook = tmp_path / "study.ipynb"
    notebook.write_text(nb(code(GOOD_FUNCS)))
    out = tmp_path / "analysis.py"
    if existing == "MARKER":
        out.write_text(MARKER + "old = 1\n")
    elif existing == "TEMPLATE":
        out.write_bytes((TEMPLATE_DIR / "analysis.py").read_bytes())
    elif existing is not None:
        out.write_text(existing)

    exit_code = main([*(["--force"] if force else []), str(notebook)])

    if written:
        assert exit_code == 0
        assert out.read_text() == MARKER + GOOD_FUNCS
    else:
        assert exit_code == 1
        assert out.read_text() == existing
        assert "--force" in capsys.readouterr().err


def test_reports_check_problems_after_writing(tmp_path, capsys):
    notebook = tmp_path / "study.ipynb"
    notebook.write_text(nb(code("def site_analysis(df):\n    return {'n': 0}\n")))
    assert main([str(notebook)]) == 1
    assert (tmp_path / "analysis.py").exists()
    assert "no aggregate(results) function" in capsys.readouterr().err
