import json
import math
from pathlib import Path

import numpy

from run_analysis.errors import AnalysisError

MAX_RESULT_BYTES = 61440


class _NotSerialisable(Exception):
    pass


class _NotFinite(Exception):
    pass


def encode(value: object) -> bytes:
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def _plain(value: object) -> object:
    """Turn numpy values into plain Python ones; reject anything JSON would coerce or refuse."""
    if isinstance(value, numpy.ndarray):
        value = value.tolist()
    elif isinstance(value, numpy.generic):
        value = value.item()
    if value is None or isinstance(value, (bool, int, str)):
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise _NotFinite
        return value
    if isinstance(value, dict):
        if not all(isinstance(k, str) for k in value):
            raise _NotSerialisable
        return {k: _plain(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_plain(v) for v in value]
    raise _NotSerialisable


def check_result(value: object, function: str = "site_analysis") -> tuple[dict, int]:
    if not isinstance(value, dict):
        raise AnalysisError("invalid_result", f"{function} must return a dict, not {type(value).__name__}")
    try:
        result = _plain(value)
    except _NotFinite:
        raise AnalysisError("invalid_result", "the returned dict is not JSON-serialisable (NaN or infinity)") from None
    except (_NotSerialisable, RecursionError):
        raise AnalysisError("invalid_result", "the returned dict is not JSON-serialisable") from None
    n = result.get("n")
    if "n" not in result:
        raise AnalysisError("invalid_result", "the returned dict has no n")
    if not isinstance(n, int) or isinstance(n, bool) or n < 0:
        raise AnalysisError("invalid_result", "n must be a non-negative integer")
    try:
        size = len(encode(result))
    except (ValueError, RecursionError):
        # For example an int of more than 4300 digits, which Python refuses to turn into text.
        raise AnalysisError("invalid_result", "the returned dict is not JSON-serialisable") from None
    if size > MAX_RESULT_BYTES:
        raise AnalysisError("result_too_large", f"the returned dict is {size} bytes; the limit is {MAX_RESULT_BYTES}")
    return result, n


def write_ok(path: Path, dataset_sha256: str | None, n: int, result: dict) -> None:
    """dataset_sha256 is None for aggregate(), which reads the hospitals' results, not a dataset."""
    doc = {"status": "ok"}
    if dataset_sha256 is not None:
        doc["datasetSha256"] = dataset_sha256
    doc.update({"n": n, "result": result})
    path.write_bytes(encode(doc))


def write_error(path: Path, code: str, message: str, dataset_sha256: str | None) -> None:
    doc = {"status": "error", "code": code, "message": message}
    if dataset_sha256 is not None:
        doc["datasetSha256"] = dataset_sha256
    path.write_bytes(encode(doc))
