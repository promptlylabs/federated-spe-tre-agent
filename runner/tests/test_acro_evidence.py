import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

from run_analysis.acro_evidence import read_evidence
from run_analysis.cli import main
from run_analysis.errors import AnalysisError

CSV = "age,sex,region\n30,F,n\n40,F,s\n50,M,n\n"
SCHEMA = [{"name": "age", "type": "integer"}, {"name": "sex", "type": "string"}, {"name": "region", "type": "string"}]


def _run(tmp_path, make_code, make_dataset, source):
    out = tmp_path / "result.json"
    assert main([str(make_code(source)), str(make_dataset(CSV, SCHEMA)), str(out)]) == 0
    return json.loads(out.read_text())


@pytest.mark.parametrize("signature, body, gets_session", [
    ("df, acro", "acro is not None", True),
    ("df, acro=None", "acro is not None", True),
    ("df", "False", False),
    ("df, *args", "len(args) > 0", False),
])
def test_session_only_for_two_positional_parameters(tmp_path, make_code, make_dataset, signature, body, gets_session):
    doc = _run(tmp_path, make_code, make_dataset,
               f"def site_analysis({signature}):\n    return {{'n': len(df), 'got': {body}}}\n")
    assert doc["status"] == "ok"
    assert doc["result"]["got"] is gets_session
    assert "acro" not in doc  # no ACRO outputs, no evidence


def test_one_parameter_code_never_imports_acro(tmp_path, make_code, make_dataset):
    code, data, out = make_code("def site_analysis(df):\n    return {'n': len(df)}\n"), make_dataset(CSV, SCHEMA), tmp_path / "r.json"
    script = f"import sys; from run_analysis.cli import main; main([{str(code)!r}, {str(data)!r}, {str(out)!r}]); print('acro' in sys.modules)"
    run = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True, env={**os.environ}, check=True)
    assert run.stdout.strip() == "False"


def test_counts_and_means_are_written_next_to_the_result(tmp_path, make_code, make_dataset):
    doc = _run(tmp_path, make_code, make_dataset,
               "def site_analysis(df, acro):\n"
               "    acro.crosstab(df.sex, df.region)\n"
               "    acro.crosstab(df.sex, df.region, values=df.age, aggfunc='mean')\n"
               "    acro.pivot_table(df, index='sex', values='age', aggfunc='count')\n"
               "    return {'n': len(df)}\n")
    assert doc["status"] == "ok"
    assert doc["acro"] == {"tables": [
        {"method": "crosstab", "statistic": "count", "rows": ["F", "M"], "columns": ["n", "s"], "count": [[1, 1], [1, 0]]},
        {"method": "crosstab", "statistic": "mean", "rows": ["F", "M"], "columns": ["n", "s"],
         "count": [[1, 1], [1, 0]], "sum": [[30.0, 40.0], [50.0, 0.0]]},
        {"method": "pivot_table", "statistic": "count", "rows": ["F", "M"], "columns": ["age"], "count": [[2], [1]]},
    ]}


@pytest.mark.parametrize("call", [
    "acro.ols(df['age'], df[['age']].assign(c=1.0))",
    "acro.crosstab(df.sex, df.region, margins=True)",
    "acro.crosstab(df.sex, df.region, margins=True, margins_name='Total')",
    "acro.pivot_table(df, index='sex', values='age', aggfunc=['mean', 'count'])",
    "acro.crosstab([df.sex, df.age], df.region)",
    "acro.pivot_table(df, index='sex', values='age', aggfunc='median')",
])
def test_outputs_that_cannot_be_pooled_fail_the_run(tmp_path, make_code, make_dataset, call):
    doc = _run(tmp_path, make_code, make_dataset,
               f"def site_analysis(df, acro):\n    acro.crosstab(df.sex, df.region)\n    {call}\n    return {{'n': len(df)}}\n")
    assert doc["status"] == "error" and doc["code"] == "invalid_result"
    assert doc["message"].startswith("ACRO output 2 ")
    assert "'F'" not in doc["message"] and " F " not in doc["message"]


def test_evidence_counts_towards_the_size_limit(tmp_path, make_code, make_dataset):
    # 61,415 bytes of dict alone fit; with the evidence of one table they do not.
    doc = _run(tmp_path, make_code, make_dataset,
               "def site_analysis(df, acro):\n    acro.crosstab(df.sex, df.region)\n    return {'n': len(df), 'pad': 'x' * 61400}\n")
    assert doc["status"] == "error" and doc["code"] == "result_too_large"


def _evidence(tmp_path: Path, tables: dict[str, str], analysis: str = "Mean", command: str = "acro.crosstab(a, b)",
              independent=("a", "b")) -> Path:
    folder = tmp_path / "evidence"
    folder.mkdir()
    (folder / "evidence.json").write_text(json.dumps({"outputs": {"output_0": {
        "command": command, "analysis_names": [analysis],
        "variable_types": {"dependent": "age", "independent": list(independent)},
        "interim_tables": {name: f"output_0_{name}.csv" for name in tables}}}}))
    for name, text in tables.items():
        (folder / f"output_0_{name}.csv").write_text(text)
    return folder


def test_an_empty_sum_cell_with_a_zero_count_is_zero(tmp_path):
    folder = _evidence(tmp_path, {"count_table": "a,x,y\np,1,0\n", "sum": "a,x,y\np,30.0,\n"})
    assert read_evidence(folder)["tables"][0]["sum"] == [[30.0, 0.0]]


@pytest.mark.parametrize("tables", [
    {"count_table": "a,x,y\np,1,2\n", "sum": "a,x,y\np,30.0,\n"},          # a missing sum where people are counted
    {"count_table": "a,x,y\np,1,q\n", "sum": "a,x,y\np,30.0,1.0\n"},       # a label where a count should be
    {"count_table": "a,x,y\np,1,2\n", "sum": "a,x\np,30.0\n"},             # sum and count disagree in shape
])
def test_evidence_that_is_not_a_table_of_numbers_fails(tmp_path, tables):
    with pytest.raises(AnalysisError) as e:
        read_evidence(_evidence(tmp_path, tables))
    assert e.value.code == "invalid_result" and e.value.message.startswith("ACRO output 1 ")


def test_margins_are_recognised_by_their_sums_whatever_their_name(tmp_path):
    # A last row and column holding the sums, as crosstab(margins=True, margins_name=...) writes them.
    folder = _evidence(tmp_path, {"count_table": "a,x,y,Total\np,12,1,13\nq,3,20,23\nTotal,15,21,36\n"},
                       analysis="FrequencyTable")
    with pytest.raises(AnalysisError, match="margins are not supported"):
        read_evidence(folder)
