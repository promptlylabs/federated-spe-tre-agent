"""Find the dataset CSV, check it against schema.json and load it as typed columns.

No message raised here holds a cell value: only column names, line numbers,
types, sizes and file names.
"""

import csv
import hashlib
import io
import json
import math
from dataclasses import dataclass
from pathlib import Path

import pandas

from run_analysis.errors import AnalysisError

MAX_CSV_BYTES = 1073741824
TYPES = ("integer", "number", "string", "boolean")
_GIB = 1024**3


@dataclass(frozen=True)
class Column:
    name: str
    type: str


def find_files(dataset_dir: Path) -> tuple[Path, Path]:
    if not dataset_dir.is_dir():
        raise AnalysisError("dataset_not_found", "the dataset directory does not exist")
    csvs = sorted(p for p in dataset_dir.iterdir() if p.is_file() and p.suffix.lower() == ".csv")
    if not csvs:
        raise AnalysisError("dataset_not_found", "no .csv file in the dataset directory")
    if len(csvs) > 1:
        names = ", ".join(p.name for p in csvs)
        raise AnalysisError("ambiguous_dataset", f"more than one .csv file in the dataset directory: {names}")
    schema = dataset_dir / "schema.json"
    if not schema.is_file():
        raise AnalysisError("schema_not_found", "no schema.json in the dataset directory")
    return csvs[0], schema


def check_size(csv_path: Path) -> None:
    size = csv_path.stat().st_size
    if size > MAX_CSV_BYTES:
        raise AnalysisError(
            "dataset_too_large",
            f"the dataset file is {size / _GIB:.1f} GiB; the limit is {MAX_CSV_BYTES / _GIB:g} GiB",
        )


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def load_schema(path: Path) -> list[Column]:
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise AnalysisError("invalid_schema", "schema.json is not valid JSON") from None
    if not isinstance(doc, dict) or not isinstance(doc.get("columns"), list):
        raise AnalysisError("invalid_schema", "schema.json has no columns list")
    columns: list[Column] = []
    for i, entry in enumerate(doc["columns"]):
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str):
            raise AnalysisError("invalid_schema", f"schema.json column {i + 1} has no name")
        name, type_ = entry["name"], entry.get("type")
        if type_ not in TYPES:
            raise AnalysisError(
                "invalid_schema", f"schema.json column '{name}' has an unknown type; use one of {', '.join(TYPES)}"
            )
        if any(c.name == name for c in columns):
            raise AnalysisError("invalid_schema", f"schema.json names column '{name}' twice")
        columns.append(Column(name, type_))
    return columns


def read_dataset(csv_path: Path, columns: list[Column]) -> pandas.DataFrame:
    try:
        # utf-8-sig drops the byte-order mark that Excel's "CSV UTF-8" export writes.
        text = csv_path.read_bytes().decode("utf-8-sig")
    except UnicodeDecodeError:
        raise AnalysisError("invalid_dataset", "the dataset file is not valid UTF-8") from None
    if not text.strip():
        raise AnalysisError("invalid_dataset", "the dataset file is empty")

    expected = [c.name for c in columns]
    header: list[str] | None = None
    rows: list[list[str]] = []
    lines: list[int] = []  # the physical line each row starts on
    reader = csv.reader(io.StringIO(text, newline=""), strict=True)
    last_line = 0
    try:
        for record in reader:
            start_line, last_line = last_line + 1, reader.line_num
            if not record:  # a blank line
                continue
            if header is None:
                header = record
                _check_header(header, expected)
                continue
            if len(record) != len(header):
                raise AnalysisError(
                    "invalid_dataset", f"line {start_line} has {len(record)} fields; the header has {len(header)}"
                )
            rows.append(record)
            lines.append(start_line)
    except csv.Error:
        raise AnalysisError("invalid_dataset", f"the dataset file is not valid CSV at line {reader.line_num}") from None

    cells = list(zip(*rows)) if rows else [()] * len(columns)
    return pandas.DataFrame(
        {c.name: _convert(c, list(cells[i]), lines) for i, c in enumerate(columns)},
        index=pandas.RangeIndex(len(rows)),
    )


def _check_header(header: list[str], expected: list[str]) -> None:
    if header == expected:
        return
    message = f"columns differ from schema.json: expected [{', '.join(expected)}]"
    # Name what was found only when it looks like a header. A file with no header
    # row, or a line above it, would otherwise put data values in the message.
    if any(name in expected for name in header):
        message += f", found [{', '.join(header)}]"
    else:
        message += "; the first line has none of these names (is the header row missing?)"
    raise AnalysisError("schema_mismatch", message)


_INT64_MIN, _INT64_MAX = -(2**63), 2**63 - 1


def _parse_integer(v: str) -> int:
    i = int(v)
    if not _INT64_MIN <= i <= _INT64_MAX:
        raise ValueError
    return i


def _parse_number(v: str) -> float:
    f = float(v)
    if not math.isfinite(f):
        raise ValueError
    return f


def _parse_boolean(v: str) -> bool:
    lowered = v.lower()
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    raise ValueError


_PARSERS = {
    "integer": (_parse_integer, "Int64", "an integer"),
    "number": (_parse_number, "Float64", "a finite number"),
    "boolean": (_parse_boolean, "boolean", "true or false"),
}


def _convert(column: Column, cells: list[str], lines: list[int]) -> pandas.Series:
    if column.type == "string":
        return pandas.Series([None if v == "" else v for v in cells], dtype="string")
    parse, dtype, what = _PARSERS[column.type]
    values = []
    for v, line in zip(cells, lines):
        if v == "":
            values.append(None)
            continue
        try:
            values.append(parse(v))
        except (ValueError, OverflowError):
            raise AnalysisError("schema_mismatch", f"column '{column.name}', line {line}: not {what}") from None
    return pandas.Series(pandas.array(values, dtype=dtype))
