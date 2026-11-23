# Runner image (`5s-tes/python-runner:1`)

The image that a researcher's Python code runs in at the hospital. The
researcher writes and tests the code in their SPE, a Jupyter notebook that
`spms-spe-operator-go` starts from the RHOAI image
`s2i-generic-data-science-notebook:2025.1`. At the hospital, a version of the
same operator, on OpenShift, starts a locked-down pod from this image to run
the code. So the image has the same Python and libraries as the SPE notebook
(Python 3.11, pandas, numpy, scipy, scikit-learn, matplotlib), plus one
command, `run-analysis`, and without pip.

The image is not yet used. Starting the pod from a federated task is PoC
task 2.4.

## Command

```sh
run-analysis [--debug] <code.py> <dataset dir> <result.json>
```

`--debug` also prints the full traceback of a failure in the researcher's
code to stderr. Use it only in the SPE, against synthetic data: a traceback
can contain data values. `result.json` is the same with or without it. The
hospital never passes `--debug`; the Agent, not the researcher, sets the
command (PoC task 2.4).

The wrapper does these steps in order. If a step fails, it writes an error
result and stops.

1. Find the one `.csv` file and `schema.json` in the dataset directory.
2. Check that the CSV is not larger than 256 MiB.
3. Read the CSV as UTF-8 with a header row. A byte-order mark (from Excel's
   "CSV UTF-8" export) is allowed. Blank lines are skipped, except in a
   one-column file, where an empty line is an empty cell (blank lines at the
   end of the file are still skipped). Every row must have as many fields as
   the header. A file that cannot be read gives `invalid_dataset`.
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

The code is one file. It can import the image's libraries, but not other
files of your own: put helper functions in the same file. (`run-analysis`
starts Python in isolated mode, so `from helpers import x` fails in the SPE
the same way it would at the hospital.)

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
An empty cell is allowed in every column and becomes a missing value. An
`integer` is ASCII digits with an optional sign (`-12`, `+7`). A `number` is a
plain decimal with an optional exponent (`3`, `-1.5`, `.5`, `2E-3`). Spaces,
digit underscores (`1_000`), other digits and `nan` or `inf` are not accepted.

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
| `dataset_too_large` | The CSV is larger than 256 MiB. |
| `invalid_dataset` | The CSV cannot be read, is empty, is not UTF-8, is not valid CSV, or a row has the wrong number of fields. |
| `schema_not_found` | No `schema.json`. |
| `invalid_schema` | `schema.json` is not valid, has an unknown type or names a column twice. |
| `schema_mismatch` | The CSV columns are not the schema's, or a value is not of its column's type. |
| `code_error` | The researcher's file could not be imported, has no `site_analysis`, or `site_analysis` raised an exception (also `sys.exit`). |
| `invalid_result` | The return value is not a dict, cannot be written as JSON, or has no valid `n`. |
| `result_too_large` | The return value is larger than 61,440 bytes. |

No message contains a value from the dataset. Messages name columns, line
numbers, types, sizes and file names. When the header differs from the
schema, the message repeats only the names that are schema columns and counts
the others (`found [age, +1 unknown]`): a file with no header row, or a line
above it, would otherwise put data in the message. Line numbers are lines in the
file, counting blank lines and lines inside quoted cells. For an exception, the message gives only
its type and the line in the researcher's file
(`site_analysis raised KeyError at analysis.py line 2`), because the
exception text can contain data values.

Exit code: 0 when `result.json` was written (ok or error), 2 for wrong
arguments, 1 when `result.json` could not be written.

## Isolation

The image runs as user 1001 by default. On OpenShift the pod runs as a random
user ID in group 0, so `/outputs` is writable by group 0.

The isolation comes from the pod that the operator starts, not from the image.
The pod must have:

- `readOnlyRootFilesystem: true`, non-root, no privilege escalation;
- a NetworkPolicy that denies all ingress and egress;
- the dataset as a read-only volume;
- `/outputs` as the only writable volume that is kept;
- a writable scratch `/tmp` (an `emptyDir`, discarded with the pod). matplotlib,
  `tempfile` and joblib need it; the image points `TMPDIR` and `MPLCONFIGDIR`
  there;
- enough memory for the dataset: parsing peaks at about 25 times the CSV size
  (1.5 GiB for 64 MiB), so about 6 GiB for a file at the 256 MiB limit.

`runner/test.sh image` checks the same things with `docker run --network none
--read-only --tmpfs /tmp`, as a stand-in for the pod.

The researcher's code can write anything to stdout and stderr, for example
`print(df)`. Only `result.json` is kept free of data values. Whatever runs the
pod (2.4) must not return the container's logs to the researcher.

pip, uv, micropipenv, virtualenv, and the pip and setuptools wheels that
`ensurepip` uses are removed, so code cannot install packages. The list of
installed libraries is in `/opt/runner/libraries.txt` in the image.

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
that the installers are gone, the user is 1001, a random user in group 0 can
write `/outputs`, Python is 3.11, and code can neither open a network
connection nor change the dataset.

The image is amd64 only. On Apple silicon, Docker runs it under emulation.

## Base image

`quay.io/opendatahub/odh-pipeline-runtime-datascience-cpu-py311-ubi9:2025a-v1.35`,
pinned by digest in the `Dockerfile`. It is the Open Data Hub data-science
runtime image, which has the notebook's libraries without Jupyter. It stands
in for the RHOAI ImageStream `s2i-generic-data-science-notebook:2025.1` that
the SPE notebooks use. RHOAI builds its 2025.1 images from Open Data Hub
2025a. The ImageStream is in the cluster, so its registry digest is not in
any repository. To find it:

```sh
oc get imagestream s2i-generic-data-science-notebook -n redhat-ods-applications \
  -o jsonpath='{.spec.tags[?(@.name=="2025.1")].from.name}'
```

Then change only the `FROM` line (and the labels), and run `runner/test.sh`.
Pulling from `registry.redhat.io` needs a Red Hat login.

The runtime image has the same data-science libraries as the Open Data Hub
2025a notebook image (`odh-workbench-jupyter-datascience-cpu-py311-ubi9:2025a-v1.35`):
pandas 2.2.3, numpy 2.2.6, scipy 1.15.3, scikit-learn 1.6.1, matplotlib
3.10.5, pyarrow 21.0.0. statsmodels and seaborn are in neither. The packages
that differ are Jupyter and infrastructure packages (ipython, ipykernel,
pyzmq, kubernetes, protobuf, psutil, urllib3, click, opentelemetry-proto),
and onnx (1.19.0 here, 1.18.0 in the notebook).

Researcher code must use only these libraries. A package that the researcher
installs in the SPE notebook does not exist at the hospital.
