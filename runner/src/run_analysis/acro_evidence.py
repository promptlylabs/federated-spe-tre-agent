"""ACRO evidence: what a federated ACRO session recorded about each table site_analysis made, in the form the
aggregator pools.

At the hospital ACRO runs in federated mode: it applies no rules and withholds nothing, it only records the counts
(and sums) behind each cell. The aggregator adds these up across hospitals and withholds pooled cells below its
minimum. Only tables it can pool are accepted: crosstab with two grouping variables, or pivot_table with one, with one
statistic out of count, sum and mean, and no margins (the aggregator adds totals). Anything else fails the run, so a
table the researcher meant to pool is never silently left out.

Messages name an output's position and kind only: labels are data values.
"""

import csv
import json
import math
import re
import shutil
import tempfile
from pathlib import Path

from run_analysis.errors import AnalysisError

_STATISTICS = {"FrequencyTable": "count", "Sum": "sum", "Mean": "mean"}
_METHOD = re.compile(r"\.(crosstab|pivot_table)\(")
_GROUPING_VARIABLES = {"crosstab": 2, "pivot_table": 1}


def collect(session) -> dict | None:
    """Finalises the session and returns its evidence as {"tables": [...]}, or None when it made no outputs."""
    tmp = Path(tempfile.mkdtemp())
    try:
        folder = tmp / "acro"  # ACRO refuses a folder that already exists
        session.finalise(str(folder), "json")
        return read_evidence(folder)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def read_evidence(folder: Path) -> dict | None:
    try:
        outputs = json.loads((folder / "evidence.json").read_text(encoding="utf-8")).get("outputs") or {}
    except (OSError, ValueError):
        return None  # no outputs: ACRO writes no evidence
    tables = [_table(position, folder, output) for position, output in enumerate(outputs.values(), start=1)]
    return {"tables": tables} if tables else None


def _fail(position: int, kind: str, reason: str) -> AnalysisError:
    return AnalysisError("invalid_result", f"ACRO output {position} ({kind}) cannot be pooled: {reason}")


def _table(position: int, folder: Path, output: dict) -> dict:
    match = _METHOD.search(output.get("command") or "")
    names = output.get("analysis_names") or []
    if match is None:
        kind = names[0] if len(names) == 1 else "unknown"
        raise _fail(position, kind, "only acro.crosstab and acro.pivot_table tables can be pooled")
    method = match.group(1)
    if len(names) != 1 or names[0] not in _STATISTICS:
        raise _fail(position, method, "use one statistic out of count, sum and mean")
    statistic = _STATISTICS[names[0]]
    independent = (output.get("variable_types") or {}).get("independent") or []
    if len(independent) != _GROUPING_VARIABLES[method]:
        raise _fail(position, method, f"{method} tables need {_GROUPING_VARIABLES[method]} grouping variable(s)")

    files = output.get("interim_tables") or {}
    rows, columns, count = _read(position, method, folder, files.get("count_table"))
    if any(not float(c).is_integer() or c < 0 for row in count for c in row):
        raise _fail(position, method, "a count is not a non-negative whole number")
    if _has_margins(rows, columns, count):
        raise _fail(position, method, "margins are not supported; the aggregator adds the totals")
    table = {"method": method, "statistic": statistic, "rows": rows, "columns": columns,
             "count": [[int(c) for c in row] for row in count]}
    if statistic != "count":
        sum_rows, sum_columns, sums = _read(position, method, folder, files.get("sum"), blank_is_nan=True)
        if (sum_rows, sum_columns) != (rows, columns):
            raise _fail(position, method, "its sums and counts have different shapes")
        for i, row in enumerate(sums):
            for j, value in enumerate(row):
                if math.isnan(value):
                    if count[i][j] != 0:
                        raise _fail(position, method, "a cell with people has no sum")
                    row[j] = 0.0
        table["sum"] = sums
    return table


def _read(position: int, method: str, folder: Path, name: str | None, blank_is_nan: bool = False):
    if not name:
        raise _fail(position, method, "ACRO recorded no table for it")
    try:
        with (folder / name).open(encoding="utf-8", newline="") as f:
            lines = list(csv.reader(f))
    except OSError:
        raise _fail(position, method, "ACRO's table could not be read") from None
    if len(lines) < 2 or len(lines[0]) < 2:
        raise _fail(position, method, "ACRO's table is empty")
    columns = lines[0][1:]
    rows, cells = [], []
    for line in lines[1:]:
        if len(line) != len(columns) + 1:
            raise _fail(position, method, "ACRO's table has rows of different lengths")
        rows.append(line[0])
        try:
            cells.append([math.nan if (v == "" and blank_is_nan) else float(v) for v in line[1:]])
        except ValueError:
            raise _fail(position, method, "ACRO's table has a cell that is not a number") from None
    if any(math.isinf(v) for row in cells for v in row) or (not blank_is_nan and any(math.isnan(v) for row in cells for v in row)):
        raise _fail(position, method, "ACRO's table has a cell that is not a finite number")
    return rows, columns, cells


def _has_margins(rows: list[str], columns: list[str], count: list[list[float]]) -> bool:
    """crosstab(margins=True) adds an "All" row and column holding the sums of the others."""
    if len(rows) < 2 or len(columns) < 2 or rows[-1] != "All" or columns[-1] != "All":
        return False
    body = [row[:-1] for row in count[:-1]]
    return [sum(r) for r in body] == [row[-1] for row in count[:-1]] and [sum(c) for c in zip(*body)] == count[-1][:-1]
