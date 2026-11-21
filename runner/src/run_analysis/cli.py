import sys
from pathlib import Path

from run_analysis.errors import AnalysisError
from run_analysis.result import write_error, write_ok

USAGE = "usage: run-analysis <code.py> <dataset dir> <result.json>"


def _run(code: Path, dataset_dir: Path, out: Path) -> None:
    if not any(p.suffix.lower() == ".csv" for p in dataset_dir.iterdir()):
        raise AnalysisError("dataset_not_found", "no .csv file in the dataset directory")
    write_ok(out, "0" * 64, 0, {})


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 3:
        print(USAGE, file=sys.stderr)
        return 2
    code, dataset_dir, out = (Path(a) for a in args)
    try:
        try:
            _run(code, dataset_dir, out)
        except AnalysisError as e:
            write_error(out, e.code, e.message, None)
    except OSError as e:
        print(f"run-analysis: could not write {out}: {type(e).__name__}", file=sys.stderr)
        return 1
    return 0
