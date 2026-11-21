"""Import the researcher's file and call its site_analysis(df).

A failure is reported by exception type and line only: the exception text can
hold data values (for example KeyError: 'F'), so it is never kept.
"""

import importlib.util
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


def call_site_analysis(code_path: Path, frame: pandas.DataFrame) -> object:
    name = code_path.name
    try:
        spec = importlib.util.spec_from_file_location("analysis", code_path)
        if spec is None or spec.loader is None:
            raise AnalysisError("code_error", f"{name} is not a Python file")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    except AnalysisError:
        raise
    except BaseException as e:
        line = _line_in(code_path, e)
        where = f" at line {line}" if line is not None else ""
        raise AnalysisError("code_error", f"importing {name} raised {type(e).__name__}{where}") from None

    site_analysis = getattr(module, "site_analysis", None)
    if not callable(site_analysis):
        raise AnalysisError("code_error", f"{name} has no site_analysis function")

    try:
        return site_analysis(frame)
    except BaseException as e:
        line = _line_in(code_path, e)
        where = f" at {name} line {line}" if line is not None else ""
        raise AnalysisError("code_error", f"site_analysis raised {type(e).__name__}{where}") from None
