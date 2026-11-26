"""check-analysis: what DAAMS runs in the researcher's SPE before it submits analysis.py.

It checks the code the way intake and the orchestrator will, then, given a synthetic copy of a dataset, dry-runs
site_analysis on it and aggregate on the result. Exit 0 means the code may be submitted, 1 that it may not.
"""

import ast

from run_analysis.errors import AnalysisError

# Intake's limit on the inline code (FederatedTesProfile.MaxCodeBytes).
MAX_CODE_BYTES = 262144

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
