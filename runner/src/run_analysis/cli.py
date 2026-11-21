import sys
from pathlib import Path

from run_analysis import dataset
from run_analysis.errors import AnalysisError
from run_analysis.researcher import call_site_analysis
from run_analysis.result import write_error, write_ok

USAGE = "usage: run-analysis <code.py> <dataset dir> <result.json>"


def _run(code: Path, dataset_dir: Path, out: Path) -> None:
    sha = None
    try:
        csv_path, schema_path = dataset.find_files(dataset_dir)
        dataset.check_size(csv_path)
        sha = dataset.file_sha256(csv_path)
        columns = dataset.load_schema(schema_path)
        frame = dataset.read_dataset(csv_path, columns)
        value = call_site_analysis(code, frame)
        if not isinstance(value, dict):
            raise AnalysisError("invalid_result", "site_analysis must return a dict")
    except AnalysisError as e:
        write_error(out, e.code, e.message, sha)
        return
    write_ok(out, sha, len(frame), value)


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 3:
        print(USAGE, file=sys.stderr)
        return 2
    code, dataset_dir, out = (Path(a) for a in args)
    try:
        _run(code, dataset_dir, out)
    except OSError as e:
        print(f"run-analysis: could not write {out}: {type(e).__name__}", file=sys.stderr)
        return 1
    return 0
