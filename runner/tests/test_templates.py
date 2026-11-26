import shutil

from run_analysis import check, notebook
from run_analysis.notebook import TEMPLATE_DIR, convert


def test_untouched_templates_are_refused_as_template(tmp_path, capsys):
    assert check.main([str(TEMPLATE_DIR / "analysis.py")]) == 1
    assert "still the template" in capsys.readouterr().out

    copy = tmp_path / "notebook_analysis.ipynb"
    shutil.copy(TEMPLATE_DIR / "notebook_analysis.ipynb", copy)
    assert notebook.main([str(copy)]) == 1
    assert (tmp_path / "analysis.py").exists()
    assert "still the template" in capsys.readouterr().err


def test_notebook_template_converts_to_the_functions_only():
    text = convert((TEMPLATE_DIR / "notebook_analysis.ipynb").read_text(encoding="utf-8"), "notebook_analysis.ipynb")
    assert "read_csv" not in text
    assert "SYNTHETIC_DATA_PATH" not in text
    assert "def site_analysis(df):" in text
    assert "def aggregate(results):" in text
