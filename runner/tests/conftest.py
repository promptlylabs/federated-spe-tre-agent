import json

import pytest


@pytest.fixture
def make_dataset(tmp_path):
    def _make(csv_text, columns):
        d = tmp_path / "dataset"
        d.mkdir(exist_ok=True)
        (d / "cohort.csv").write_text(csv_text)
        if columns is not None:
            (d / "schema.json").write_text(json.dumps({"columns": columns}))
        return d

    return _make


@pytest.fixture
def make_code(tmp_path):
    def _make(source):
        p = tmp_path / "analysis.py"
        p.write_text(source)
        return p

    return _make
