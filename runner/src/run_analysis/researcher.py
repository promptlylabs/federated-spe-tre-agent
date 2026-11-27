"""Import the researcher's file and call one of its functions: site_analysis(df) at a hospital, aggregate(results) at
the orchestrator.

A failure is reported by exception type and line only: the exception text can
hold data values (for example KeyError: 'F'), so it is never kept in
result.json. With debug=True (the --debug flag, for the researcher's SPE with
synthetic data only) the full traceback is also printed to stderr.
"""

import importlib.util
import inspect
import sys
import traceback
from pathlib import Path

import pandas

from run_analysis.errors import AnalysisError


_MISSING = object()


def _line_in(code_path: Path, exc: BaseException) -> int | None:
    if isinstance(exc, SyntaxError) and exc.filename == str(code_path):
        return exc.lineno
    line = None
    tb = exc.__traceback__
    while tb is not None:
        if tb.tb_frame.f_code.co_filename == str(code_path):
            line = tb.tb_lineno
        tb = tb.tb_next
    return line


def call_site_analysis(code_path: Path, frame: pandas.DataFrame, debug: bool = False) -> tuple[object, object | None]:
    """Calls site_analysis(df), or site_analysis(df, acro) with a federated ACRO session when it takes two positional
    parameters. Returns what it returned and the session (None when it took one parameter)."""
    return _call(code_path, "site_analysis", frame, debug, offer_acro=True)


def call_function(code_path: Path, function: str, arg: object, debug: bool = False) -> object:
    return _call(code_path, function, arg, debug, offer_acro=False)[0]


def _takes_two_positional(func) -> bool:
    try:
        parameters = inspect.signature(func).parameters.values()
    except (TypeError, ValueError):
        return False
    positional = (inspect.Parameter.POSITIONAL_ONLY, inspect.Parameter.POSITIONAL_OR_KEYWORD)
    return sum(p.kind in positional for p in parameters) >= 2


def _new_acro_session():
    """A federated session: it records evidence and applies no rules (the aggregator does). A failure here is the
    image's, not the researcher's code."""
    try:
        from acro import ACRO

        return ACRO(suppress=False, federated=True)
    except Exception:
        raise AnalysisError("internal_error", "ACRO is not available in the runner") from None


def _call(code_path: Path, function: str, arg: object, debug: bool, offer_acro: bool) -> tuple[object, object | None]:
    name = code_path.name
    # Python records the absolute path in tracebacks and SyntaxError, so compare against it: the SPE dry run passes
    # a relative path (run-analysis analysis.py ...).
    resolved = code_path.resolve()
    # Registered while the code runs, as a normal import would be: dataclasses, pickle and joblib look the module up
    # by name. The previous entry, if any, is put back afterwards.
    spec = importlib.util.spec_from_file_location("analysis", resolved)
    if spec is None or spec.loader is None:
        raise AnalysisError("code_error", f"{name} is not a Python file")
    previous = sys.modules.get("analysis", _MISSING)
    try:
        try:
            module = importlib.util.module_from_spec(spec)
            sys.modules["analysis"] = module
            spec.loader.exec_module(module)
        # Everything the researcher's code raises is reported by type and line, including the wrapper's own
        # AnalysisError: otherwise the code could choose the message written to result.json.
        except BaseException as e:
            if debug:
                traceback.print_exception(e, file=sys.stderr)
            line = _line_in(resolved, e)
            where = f" at line {line}" if line is not None else ""
            raise AnalysisError("code_error", f"importing {name} raised {type(e).__name__}{where}") from None

        func = getattr(module, function, None)
        if not callable(func):
            raise AnalysisError("code_error", f"{name} has no {function} function")

        session = _new_acro_session() if offer_acro and _takes_two_positional(func) else None
        try:
            return (func(arg) if session is None else func(arg, session)), session
        except BaseException as e:
            if debug:
                traceback.print_exception(e, file=sys.stderr)
            line = _line_in(resolved, e)
            where = f" at {name} line {line}" if line is not None else ""
            raise AnalysisError("code_error", f"{function} raised {type(e).__name__}{where}") from None
    finally:
        if previous is _MISSING:
            sys.modules.pop("analysis", None)
        else:
            sys.modules["analysis"] = previous
