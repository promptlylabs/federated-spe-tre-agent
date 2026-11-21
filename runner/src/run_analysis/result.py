import json
from pathlib import Path


def encode(value: object) -> bytes:
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def write_ok(path: Path, dataset_sha256: str, n: int, result: dict) -> None:
    path.write_bytes(encode({"status": "ok", "datasetSha256": dataset_sha256, "n": n, "result": result}))


def write_error(path: Path, code: str, message: str, dataset_sha256: str | None) -> None:
    doc = {"status": "error", "code": code, "message": message}
    if dataset_sha256 is not None:
        doc["datasetSha256"] = dataset_sha256
    path.write_bytes(encode(doc))
