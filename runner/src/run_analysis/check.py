"""check-analysis: what DAAMS runs in the researcher's SPE before it submits analysis.py.

It checks the code the way intake and the orchestrator will, then, given a synthetic copy of a dataset, dry-runs
site_analysis on it and aggregate on the result. Exit 0 means the code may be submitted, 1 that it may not.
"""

import ast
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path

import run_analysis
from run_analysis.errors import AnalysisError

# Intake's limit on the inline code (FederatedTesProfile.MaxCodeBytes).
MAX_CODE_BYTES = 262144

# run-analysis error codes that mean the synthetic copy itself is broken, not the researcher's code: the dry run is
# skipped instead of blocking the submission. The hospitals still check the code against the real data.
DATASET_PROBLEMS = frozenset(
    {"dataset_not_found", "ambiguous_dataset", "invalid_dataset", "invalid_schema", "schema_mismatch", "schema_not_found"}
)

DEFAULT_TIMEOUT_SECONDS = 300

USAGE = "usage: check-analysis [--dataset <synthetic dataset dir>] [--timeout <seconds>] <analysis.py>"

_FUNCTIONS = (("site_analysis", "site_analysis(df)"), ("aggregate", "aggregate(results)"))


def _invalid(message: str) -> AnalysisError:
    return AnalysisError("invalid_code", message)


def _is_template(function: ast.FunctionDef) -> bool:
    """True if the body, after an optional docstring, only raises NotImplementedError: the unfilled template."""
    body = function.body
    if body and isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant) \
            and isinstance(body[0].value.value, str):
        body = body[1:]
    if len(body) != 1 or not isinstance(body[0], ast.Raise):
        return False
    exc = body[0].exc
    if isinstance(exc, ast.Call):
        exc = exc.func
    return isinstance(exc, ast.Name) and exc.id == "NotImplementedError"


def check_code(source: bytes, name: str = "analysis.py") -> None:
    """Raises AnalysisError("invalid_code", ...) on the first reason intake or the orchestrator would refuse the code."""
    if len(source) > MAX_CODE_BYTES:
        raise _invalid(f"{name} is {len(source)} bytes; the code may be at most {MAX_CODE_BYTES} bytes")
    try:
        text = source.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise _invalid(f"{name} is not UTF-8 text") from None
    try:
        tree = ast.parse(text, filename=name)
    except SyntaxError as e:
        raise _invalid(f"{name} has a syntax error at line {e.lineno}") from None

    defined = {node.name: node for node in tree.body if isinstance(node, ast.FunctionDef)}
    for function, signature in _FUNCTIONS:
        if function not in defined:
            raise _invalid(f"{name} has no {signature} function; define it at the top level of the file, "
                           f"as 'def {signature}:'")
    for function, _ in _FUNCTIONS:
        if _is_template(defined[function]):
            raise _invalid(f"{function} is still the template; write your analysis in it")


# Runs run-analysis isolated (-I, like the run-analysis shim). -I also ignores PYTHONPATH, so the package's location is
# passed explicitly.
_BOOT = "import sys; sys.path.insert(0, sys.argv.pop(1)); from run_analysis.cli import main; sys.exit(main())"
_PACKAGE_ROOT = str(Path(run_analysis.__file__).resolve().parent.parent)


def _run_analysis(args: list[str], cwd: Path, timeout_seconds: float, function: str, where: str) -> dict:
    out = Path(args[-1])
    try:
        # The researcher's prints and warnings are captured, never mixed into check-analysis's own output.
        subprocess.run([sys.executable, "-I", "-c", _BOOT, _PACKAGE_ROOT, *args], cwd=cwd, capture_output=True,
                       timeout=timeout_seconds, check=False)
    except subprocess.TimeoutExpired:
        raise AnalysisError("timeout", f"{where}: {function} did not finish within {timeout_seconds:g} seconds") from None
    try:
        return json.loads(out.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        raise AnalysisError("internal_error", f"{where}: run-analysis did not write a result") from None


def dry_run(code: bytes, dataset_dir: Path, timeout_seconds: float) -> str | None:
    """Runs site_analysis on the synthetic dataset, then aggregate on that result as if two hospitals had returned it.

    Returns None if both passed, or the reason the dry run was skipped (the synthetic data could not be used). Raises
    AnalysisError if the code failed.
    """
    where = f"dry run on {dataset_dir.name}"
    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        # The bytes that were checked and hashed, under the name messages use, so a later edit can't change the run.
        (work / "analysis.py").write_bytes(code)
        site = _run_analysis(["analysis.py", str(dataset_dir.resolve()), str(work / "result.json")], work,
                             timeout_seconds, "site_analysis", where)
        if site.get("status") != "ok":
            if site.get("code") in DATASET_PROBLEMS:
                return f"the synthetic data could not be used: {site.get('message')}"
            raise AnalysisError(site.get("code", "internal_error"), f"{where}: {site.get('message')}")
        # Two identical results: a study has at least two hospitals, and aggregate must cope with equal values.
        (work / "results.json").write_text(json.dumps([site["result"], site["result"]]), encoding="utf-8")
        pooled = _run_analysis(["--aggregate", "analysis.py", str(work / "results.json"), str(work / "pooled.json")],
                               work, timeout_seconds, "aggregate", where)
        if pooled.get("status") != "ok":
            raise AnalysisError(pooled.get("code", "internal_error"), f"{where}: {pooled.get('message')}")
    return None


def _parse(args: list[str]) -> tuple[Path, Path | None, float] | None:
    dataset, timeout, files = None, float(DEFAULT_TIMEOUT_SECONDS), []
    while args:
        arg = args.pop(0)
        if arg in ("--dataset", "--timeout") and args:
            value = args.pop(0)
            if arg == "--dataset":
                dataset = Path(value)
            else:
                try:
                    timeout = float(value)
                except ValueError:
                    return None
                if timeout <= 0:
                    return None
        elif arg.startswith("--"):
            return None
        else:
            files.append(Path(arg))
    if len(files) != 1:
        return None
    return files[0], dataset, timeout


def main(argv: list[str] | None = None) -> int:
    parsed = _parse(list(sys.argv[1:] if argv is None else argv))
    if parsed is None:
        print(USAGE, file=sys.stderr)
        return 2
    path, dataset, timeout = parsed
    # A wrong path is a mistake in the call, not broken synthetic data: it must not pass without a dry run.
    if dataset is not None and not dataset.is_dir():
        print(f"check-analysis: {dataset} is not a directory\n{USAGE}", file=sys.stderr)
        return 2
    try:
        source = path.read_bytes()
    except OSError as e:
        print(f"{path.name} could not be read ({type(e).__name__})")
        return 1
    try:
        check_code(source, path.name)
        skipped = dry_run(source, dataset, timeout) if dataset is not None else "no synthetic data"
    except AnalysisError as e:
        print(e.message)
        return 1
    print(f"{path.name} passed (dry run on {dataset.name})" if skipped is None
          else f"{path.name} passed (no dry run: {skipped})")
    print(f"code_sha256 {hashlib.sha256(source).hexdigest()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
