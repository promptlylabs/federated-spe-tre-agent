# Runner image (`5s-tes/python-runner:1`)

The image that a researcher's Python code runs in at the hospital. It is the
Open Data Hub data-science runtime image (Python 3.11, pandas, numpy, scipy,
scikit-learn, matplotlib) with one addition, the `run-analysis` wrapper, and
with pip removed.

The image is not yet used by the Agent. Running it from a federated task is
PoC task 2.4.

## Command

```sh
run-analysis <code.py> <dataset dir> <result.json>
```

The wrapper does these steps in order. If a step fails, it writes an error
result and stops.

1. Find the one `.csv` file and `schema.json` in the dataset directory.
2. Check that the CSV is not larger than 1 GiB.
3. Read the CSV as UTF-8 with a header row.
4. Check the CSV against `schema.json`: the same column names in the same
   order, and every value is of the column's type. Then convert the columns
   to those types.
5. Import `code.py` and call `site_analysis(df)`.
6. Check the return value (see below).
7. Write `result.json` with the SHA-256 of the CSV bytes.

The researcher's file must define:

```python
def site_analysis(df):  # df is a pandas DataFrame with typed columns
    return {"n": len(df), "mean_age": float(df["age"].mean())}
```

The return value must be a dict with string keys that can be written as JSON
(no NaN or infinity). It must contain `n`, a non-negative integer: the number
of people the result is based on. Its compact UTF-8 JSON must be 61,440 bytes
(60 KiB) or less. numpy numbers and arrays are converted to plain numbers and
lists.

## `schema.json`

```json
{"columns": [{"name": "age", "type": "integer"}, {"name": "sex", "type": "string"}]}
```

The types are `integer`, `number`, `string` and `boolean` (`true` or `false`,
any case). The pandas dtypes are `Int64`, `Float64`, `string` and `boolean`.
An empty cell is allowed in every column and becomes a missing value. `nan`
and `inf` are not numbers.

## `result.json`

```json
{"status": "ok", "datasetSha256": "<hex>", "n": 120, "result": {"n": 120, "mean_age": 41.2}}
{"status": "error", "datasetSha256": "<hex>", "code": "schema_mismatch", "message": "column 'age', line 7: not an integer"}
```

An error result has `datasetSha256` only when the CSV was read.

| Code | Cause |
|---|---|
| `dataset_not_found` | No `.csv` file in the dataset directory, or no directory. |
| `ambiguous_dataset` | More than one `.csv` file. |
| `dataset_too_large` | The CSV is larger than 1 GiB. |
| `invalid_dataset` | The CSV is empty, is not UTF-8 or is not valid CSV. |
| `schema_not_found` | No `schema.json`. |
| `invalid_schema` | `schema.json` is not valid, has an unknown type or names a column twice. |
| `schema_mismatch` | The CSV columns are not the schema's, or a value is not of its column's type. |
| `code_error` | The researcher's file could not be imported, has no `site_analysis`, or `site_analysis` raised an exception (also `sys.exit`). |
| `invalid_result` | The return value is not a dict, cannot be written as JSON, or has no valid `n`. |
| `result_too_large` | The return value is larger than 61,440 bytes. |

No message contains a value from the dataset. Messages name columns, line
numbers, types, sizes and file names. For an exception, the message gives only
its type and the line in the researcher's file
(`site_analysis raised KeyError at analysis.py line 2`), because the
exception text can contain data values.

Exit code: 0 when `result.json` was written (ok or error), 2 for wrong
arguments, 1 when `result.json` could not be written.

## Isolation

The image runs as user 1001. The Agent must run it with `--network none
--read-only`, the dataset mounted read-only, and `/outputs` as the only
writable path. pip, micropipenv, and the pip wheels that `ensurepip` uses are
removed, so code cannot install packages. The list of installed libraries is
in `/opt/runner/libraries.txt` in the image.

Known PoC limit: the researcher's code runs in the same Python process as the
wrapper, so it can change the wrapper's behaviour, for example write
`result.json` itself. Output approval still sees the file before it leaves the
hospital.

## Try it

```sh
docker build --platform linux/amd64 -t 5s-tes/python-runner:1 runner
docker run --rm --platform linux/amd64 --network none --read-only \
  -v "$PWD/runner/examples:/in:ro" -v "$PWD/out:/outputs" \
  5s-tes/python-runner:1 run-analysis /in/analysis.py /in/cohort /outputs/result.json
```

`out/` must exist and be writable by user 1001 (for example `chmod 0777 out`).

In the researcher's SPE, the same command works against synthetic data:

```sh
run-analysis analysis.py ./synthetic/cohort ./result.json
```

## Tests

```sh
runner/test.sh          # unit tests, then image checks
runner/test.sh unit     # unit tests only; extra arguments go to pytest (-k dataset)
runner/test.sh image    # build the image and check isolation
```

The unit tests run in a `test` stage of the Dockerfile, on the image's own
Python and pandas. The image checks run the example locked down, and check
that pip is gone, the user is 1001, Python is 3.11, and code can neither open
a network connection nor change the dataset.

The image is amd64 only. On Apple silicon, Docker runs it under emulation.

## Base image

`quay.io/opendatahub/workbench-images:runtime-datascience-ubi9-python-3.11-2024b-20250904`,
pinned by digest in the `Dockerfile`. It stands in for the RHOAI
`s2i-generic-data-science-notebook` image that the SPE offers, so that code
that works in the SPE also works at the hospital. When SPMS confirms the exact
image, change only the `FROM` line, then run `runner/test.sh`.

The runtime image has the same data-science libraries as the Jupyter variant
of the same tag (`jupyter-datascience-ubi9-python-3.11-2024b-20250904`):
pandas 2.2.3, numpy 2.1.3, scipy 1.14.1, scikit-learn 1.5.2, matplotlib 3.9.4.
Only Jupyter and infrastructure packages differ (ipython, ipykernel, tornado,
pyzmq, kubernetes, protobuf, psutil, MarkupSafe). statsmodels is in neither.
