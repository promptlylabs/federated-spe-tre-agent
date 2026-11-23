"""Import the researcher's file and call its site_analysis(df).

A failure is reported by exception type and line only: the exception text can
hold data values (for example KeyError: 'F'), so it is never kept in
result.json. With debug=True (the --debug flag, for the researcher's SPE with
synthetic data only) the full traceback is also printed to stderr.
"""

import importlib.util
import sys
import traceback
from pathlib import Path

import pandas

from run_analysis.errors import AnalysisError


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


def call_site_analysis(code_path: Path, frame: pandas.DataFrame, debug: bool = False) -> object:
    name = code_path.name
    # Python records the absolute path in tracebacks and SyntaxError, so compare against it: the SPE dry run passes
    # a relative path (run-analysis analysis.py ...).
    resolved = code_path.resolve()
    # Registered while the code runs, as a normal import would be: dataclasses, pickle and joblib look the module up
    # by name. The previous entry, if any, is put back afterwards.
    previous = sys.modules.get("analysis")
    try:
        try:
            spec = importlib.util.spec_from_file_location("analysis", resolved)
            if spec is None or spec.loader is None:
                raise AnalysisError("code_error", f"{name} is not a Python file")
            module = importlib.util.module_from_spec(spec)
            sys.modules["analysis"] = module
            spec.loader.exec_module(module)
        except AnalysisError:
            raise
        except BaseException as e:
            if debug:
                traceback.print_exception(e, file=sys.stderr)
            line = _line_in(resolved, e)
            where = f" at line {line}" if line is not None else ""
            raise AnalysisError("code_error", f"importing {name} raised {type(e).__name__}{where}") from None

        site_analysis = getattr(module, "site_analysis", None)
        if not callable(site_analysis):
            raise AnalysisError("code_error", f"{name} has no site_analysis function")

        try:
            return site_analysis(frame)
        except BaseException as e:
            if debug:
                traceback.print_exception(e, file=sys.stderr)
            line = _line_in(resolved, e)
            where = f" at {name} line {line}" if line is not None else ""
            raise AnalysisError("code_error", f"site_analysis raised {type(e).__name__}{where}") from None
    finally:
        if previous is None:
            sys.modules.pop("analysis", None)
        else:
            sys.modules["analysis"] = previous
