import hashlib
import json

import pytest

from run_analysis import dataset
from run_analysis.cli import main
from run_analysis.dataset import Column, read_dataset

AGE_SEX = [{"name": "age", "type": "integer"}, {"name": "sex", "type": "string"}]


@pytest.fixture
def run(tmp_path, make_code):
    def _run(dataset_dir):
        out = tmp_path / "result.json"
        assert main([str(make_code("")), str(dataset_dir), str(out)]) == 0
        text = out.read_text()
        return json.loads(text), text

    return _run


def test_no_csv_is_dataset_not_found(tmp_path, run):
    d = tmp_path / "d"
    d.mkdir()
    (d / "schema.json").write_text(json.dumps({"columns": AGE_SEX}))
    assert run(d)[0]["code"] == "dataset_not_found"


def test_two_csvs_is_ambiguous_dataset(make_dataset, run):
    d = make_dataset("age,sex\n1,F\n", AGE_SEX)
    (d / "other.CSV").write_text("age,sex\n1,F\n")
    assert run(d)[0]["code"] == "ambiguous_dataset"


def test_missing_schema_is_schema_not_found(make_dataset, run):
    assert run(make_dataset("age,sex\n1,F\n", None))[0]["code"] == "schema_not_found"


def test_oversized_csv_is_dataset_too_large(make_dataset, run, monkeypatch):
    monkeypatch.setattr(dataset, "MAX_CSV_BYTES", 10)
    r, _ = run(make_dataset("age,sex\n1,F\n2,M\n", AGE_SEX))
    assert r["code"] == "dataset_too_large"
    assert "datasetSha256" not in r


def test_sha256_is_of_the_csv_bytes(make_dataset, run):
    r, _ = run(make_dataset("age\n1\n", [{"name": "age", "type": "integer"}]))
    assert r["datasetSha256"] == hashlib.sha256(b"age\n1\n").hexdigest()


@pytest.mark.parametrize(
    "schema_text",
    [
        "{",
        '{"columns": 3}',
        '{"columns": [{"name": "age", "type": "date"}]}',
        '{"columns": [{"name": "age", "type": "integer"}, {"name": "age", "type": "string"}]}',
    ],
)
def test_invalid_schema(make_dataset, run, schema_text):
    d = make_dataset("age\n1\n", None)
    (d / "schema.json").write_text(schema_text)
    assert run(d)[0]["code"] == "invalid_schema"


def test_columns_differ_is_schema_mismatch(make_dataset, run):
    r, _ = run(make_dataset("age,gender\n1,F\n", AGE_SEX))
    assert r["code"] == "schema_mismatch"
    assert r["message"] == "columns differ from schema.json: expected [age, sex], found [age, +1 unknown]"
    assert "datasetSha256" in r


def test_duplicate_header_is_schema_mismatch(make_dataset, run):
    r, _ = run(make_dataset("age,age\n1,2\n", AGE_SEX))
    assert r["code"] == "schema_mismatch"
    assert "found [age, age]" in r["message"]


def test_bad_integer_names_column_and_line_not_value(make_dataset, run):
    r, text = run(make_dataset("age\n30\nSECRET\n", [{"name": "age", "type": "integer"}]))
    assert r["code"] == "schema_mismatch"
    assert r["message"] == "column 'age', line 3: not an integer"
    assert "SECRET" not in text


def test_nan_text_is_schema_mismatch(make_dataset, run):
    r, _ = run(make_dataset("bmi\n21.5\nnan\n", [{"name": "bmi", "type": "number"}]))
    assert r["code"] == "schema_mismatch"


def test_empty_cells_become_missing(make_dataset):
    d = make_dataset("age,sex\n,F\n30,\n", AGE_SEX)
    frame = read_dataset(d / "cohort.csv", [Column("age", "integer"), Column("sex", "string")])
    assert frame.age.isna().tolist() == [True, False]
    assert frame.sex.isna().tolist() == [False, True]
    assert str(frame.age.dtype) == "Int64"
    assert str(frame.sex.dtype) == "string"


def test_types_convert(make_dataset):
    d = make_dataset("a,b,c\n1,1.5,TRUE\n2,2,false\n", None)
    frame = read_dataset(
        d / "cohort.csv", [Column("a", "integer"), Column("b", "number"), Column("c", "boolean")]
    )
    assert str(frame.a.dtype) == "Int64" and frame.a.tolist() == [1, 2]
    assert str(frame.b.dtype) == "Float64" and frame.b.tolist() == [1.5, 2.0]
    assert str(frame.c.dtype) == "boolean" and frame.c.tolist() == [True, False]


def test_not_utf8_is_invalid_dataset(make_dataset, run):
    d = make_dataset("", [{"name": "age", "type": "integer"}])
    (d / "cohort.csv").write_bytes(b"age\n\xff\n")
    assert run(d)[0]["code"] == "invalid_dataset"


def test_ragged_row_is_invalid_dataset_with_line(make_dataset, run):
    r, _ = run(make_dataset("age,sex\n1,F\n2,F,x\n", AGE_SEX))
    assert r["code"] == "invalid_dataset"
    assert "line 3" in r["message"]


# Final-review fixes


def test_missing_header_does_not_echo_the_first_row(make_dataset, run):
    r, text = run(make_dataset("42,SECRET\n50,M\n", AGE_SEX))
    assert r["code"] == "schema_mismatch"
    assert "SECRET" not in text and "42" not in r["message"]
    assert "expected [age, sex]" in r["message"]


def test_preamble_line_does_not_echo(make_dataset, run):
    r, text = run(make_dataset("Exported by SECRET\nage,sex\n30,F\n", AGE_SEX))
    assert r["code"] == "schema_mismatch"
    assert "SECRET" not in text


def test_extra_field_on_every_row_is_invalid_dataset(make_dataset, run):
    schema = [{"name": "site", "type": "string"}, {"name": "sex", "type": "string"}]
    r, _ = run(make_dataset("site,sex\nA,F,X\nB,M,Y\n", schema))
    assert r["code"] == "invalid_dataset"
    assert "line 2" in r["message"]


def test_short_row_is_invalid_dataset(make_dataset, run):
    r, _ = run(make_dataset("age,sex\n42,F\n50\n", AGE_SEX))
    assert r["code"] == "invalid_dataset"
    assert "line 3" in r["message"]


def test_integer_beyond_int64_is_schema_mismatch(make_dataset, run):
    r, _ = run(make_dataset("age\n99999999999999999999\n", [{"name": "age", "type": "integer"}]))
    assert r["code"] == "schema_mismatch"
    assert r["message"] == "column 'age', line 2: not an integer"


def test_utf8_bom_is_accepted(make_dataset):
    d = make_dataset("", None)
    (d / "cohort.csv").write_bytes(b"\xef\xbb\xbfage,sex\n30,F\n")
    frame = read_dataset(d / "cohort.csv", [Column("age", "integer"), Column("sex", "string")])
    assert frame.age.tolist() == [30]


def test_line_numbers_count_blank_lines_and_multiline_cells(make_dataset, run):
    r, _ = run(make_dataset('age,sex\n\n30,"F\nX"\nbad,F\n', AGE_SEX))
    assert r["message"] == "column 'age', line 5: not an integer"


# Code-review fixes (PR #15)


def test_unknown_header_names_are_never_echoed(make_dataset, run):
    # No header row; one data cell happens to equal a column name.
    schema = [{"name": "name", "type": "string"}, {"name": "diagnosis", "type": "string"}]
    r, text = run(make_dataset("diagnosis,John Smith\nx,y\n", schema))
    assert r["code"] == "schema_mismatch"
    assert "John Smith" not in text
    assert r["message"] == "columns differ from schema.json: expected [name, diagnosis], found [diagnosis, +1 unknown]"


def test_single_column_empty_line_is_a_missing_value(make_dataset):
    d = make_dataset("age\n30\n\n40\n", None)
    frame = read_dataset(d / "cohort.csv", [Column("age", "integer")])
    assert frame.age.isna().tolist() == [False, True, False]


def test_single_column_trailing_blank_lines_are_not_rows(make_dataset):
    d = make_dataset("age\n30\n40\n\n\n", None)
    frame = read_dataset(d / "cohort.csv", [Column("age", "integer")])
    assert frame.age.tolist() == [30, 40]


def test_multi_column_blank_lines_are_still_skipped(make_dataset):
    d = make_dataset("age,sex\n30,F\n\n40,M\n", None)
    frame = read_dataset(d / "cohort.csv", [Column("age", "integer"), Column("sex", "string")])
    assert frame.age.tolist() == [30, 40]


def test_unreadable_dataset_is_an_error_result(make_dataset, run, monkeypatch):
    def denied(path):
        raise PermissionError(13, "Permission denied", str(path))

    monkeypatch.setattr(dataset, "file_sha256", denied)
    r, _ = run(make_dataset("age\n1\n", [{"name": "age", "type": "integer"}]))
    assert r["code"] == "invalid_dataset"
    assert r["message"] == "the dataset could not be read (PermissionError)"


def test_deeply_nested_schema_is_invalid_schema(make_dataset, run):
    d = make_dataset("age\n1\n", None)
    (d / "schema.json").write_text("[" * 100000 + "]" * 100000)
    assert run(d)[0]["code"] == "invalid_schema"


def test_size_limit_is_256_mib():
    assert dataset.MAX_CSV_BYTES == 256 * 1024 * 1024


@pytest.mark.parametrize("cell", ["1_000", " 42", "42 ", "٣٠", "+", "0x1A", "1e3", "1.0"])
def test_integer_cells_must_be_plain_ascii_digits(make_dataset, run, cell):
    r, _ = run(make_dataset(f"age\n{cell}\n", [{"name": "age", "type": "integer"}]))
    assert r["code"] == "schema_mismatch"
    assert r["message"] == "column 'age', line 2: not an integer"


@pytest.mark.parametrize("cell", ["1_000.5", " 1.5", "1.5 ", "١.٥", "0x1p3", ".", "1.", "infinity"])
def test_number_cells_must_be_plain_ascii_decimals(make_dataset, run, cell):
    r, _ = run(make_dataset(f"bmi\n{cell}\n", [{"name": "bmi", "type": "number"}]))
    assert r["code"] == "schema_mismatch"
    assert r["message"] == "column 'bmi', line 2: not a finite number"


def test_plain_numbers_still_parse(make_dataset):
    d = make_dataset("a,b\n-12,-1.5\n+7,3\n0,.5\n007,2E-3\n", None)
    frame = read_dataset(d / "cohort.csv", [Column("a", "integer"), Column("b", "number")])
    assert frame.a.tolist() == [-12, 7, 0, 7]
    assert frame.b.tolist() == [-1.5, 3.0, 0.5, 0.002]


def test_long_string_cells_are_accepted(make_dataset):
    d = make_dataset("note\n" + "x" * 200_000 + "\n", None)
    frame = read_dataset(d / "cohort.csv", [Column("note", "string")])
    assert len(frame.note[0]) == 200_000


def test_unreadable_schema_is_invalid_schema(make_dataset, run, monkeypatch):
    def denied(path):
        raise PermissionError(13, "Permission denied", str(path))

    monkeypatch.setattr(dataset, "load_schema", denied)
    r, _ = run(make_dataset("age\n1\n", [{"name": "age", "type": "integer"}]))
    assert r["code"] == "invalid_schema"
    assert r["message"] == "schema.json could not be read (PermissionError)"
