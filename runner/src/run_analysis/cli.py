import json
import sys
from pathlib import Path

from run_analysis import dataset
from run_analysis.errors import AnalysisError
from run_analysis.researcher import call_function, call_site_analysis
from run_analysis.result import check_result, write_error, write_ok

USAGE = (
    "usage: run-analysis [--debug] <code.py> <dataset dir> <result.json>\n"
    "       run-analysis [--debug] --aggregate <code.py> <results.json> <result.json>"
)


def _run(code: Path, dataset_dir: Path, out: Path, debug: bool) -> None:
    sha = None
    try:
        # A read failure (for example a file the pod user cannot read) is an error result, not "could not write".
        try:
            csv_path, schema_path = dataset.find_files(dataset_dir)
            dataset.check_size(csv_path)
            sha = dataset.file_sha256(csv_path)
        except OSError as e:
            raise AnalysisError("invalid_dataset", f"the dataset could not be read ({type(e).__name__})") from None
        try:
            columns = dataset.load_schema(schema_path)
        except OSError as e:
            raise AnalysisError("invalid_schema", f"schema.json could not be read ({type(e).__name__})") from None
        try:
            frame = dataset.read_dataset(csv_path, columns)
        except OSError as e:
            raise AnalysisError("invalid_dataset", f"the dataset could not be read ({type(e).__name__})") from None
        result, n = check_result(call_site_analysis(code, frame, debug))
    except AnalysisError as e:
        write_error(out, e.code, e.message, sha)
        return
    write_ok(out, sha, n, result)


def _read_results(path: Path) -> list[dict]:
    """The hospitals' results: a JSON array of the dicts each site_analysis returned."""
    try:
        results = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        results = None
    if not isinstance(results, list) or not all(isinstance(r, dict) for r in results):
        raise AnalysisError("invalid_dataset", "the hospital results could not be read")
    return results


def _aggregate(code: Path, results_path: Path, out: Path, debug: bool) -> None:
    try:
        results = _read_results(results_path)
        result, n = check_result(call_function(code, "aggregate", results, debug), "aggregate")
    except AnalysisError as e:
        write_error(out, e.code, e.message, None)
        return
    write_ok(out, None, n, result)


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    # --debug prints the researcher's full traceback to stderr. Only for the
    # SPE with synthetic data: the traceback can hold data values.
    debug = bool(args) and args[0] == "--debug"
    if debug:
        args = args[1:]
    # --aggregate runs aggregate(results) on the hospitals' results (at the orchestrator) instead of site_analysis(df).
    aggregate = bool(args) and args[0] == "--aggregate"
    if aggregate:
        args = args[1:]
    if len(args) != 3:
        print(USAGE, file=sys.stderr)
        return 2
    code, data, out = (Path(a) for a in args)
    try:
        if aggregate:
            _aggregate(code, data, out, debug)
        else:
            _run(code, data, out, debug)
    except OSError as e:
        print(f"run-analysis: could not write {out}: {type(e).__name__}", file=sys.stderr)
        return 1
    return 0
