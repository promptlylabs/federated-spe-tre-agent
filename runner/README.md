# Runner image (`python-runner:1`)

The image that a researcher's Python code runs in at the hospital. The
researcher writes and tests the code in their SPE, a Jupyter notebook that
`spms-spe-operator-go` starts. At the hospital, a version of the same operator,
on OpenShift, starts a locked-down pod from this image to run the code. The
image must have the same Python and libraries as the SPE notebook, plus one
command, `run-analysis`, and no package installers.

**The base is a placeholder and does not match the SPE yet.** This image is
built on Open Data Hub 2025a with Python 3.11. The SPE actually runs Open Data
Hub 2025b with Python 3.12 (`s2i-generic-data-science-notebook:2025.2`, public
build `odh-workbench-jupyter-datascience-cpu-py312-ubi9:2025b-v1.40`), plus
ACRO 1.1.0 with SPMS's disclosure thresholds. The image must be moved to that
base, with ACRO and the same thresholds. That move is still to do. Code tasks
already run on the placeholder base, so until it is done, code that works in the
SPE may fail at the hospital, for example any code that uses `acro`. See "Base
image" below.

The Agent runs this image for a federated code task. On the dev stack it
starts a Docker container with the restrictions under "Isolation" (see
[`docs/federated-tasks.md`](../docs/federated-tasks.md), "Running a code task",
and [`dev/federated`](../dev/federated/README.md)). In production the hospital
operator is the runtime: it starts the pod with the same restrictions.

## Command

```sh
run-analysis [--debug] <code.py> <dataset dir> <result.json>
```

`--debug` also prints the full traceback of a failure in the researcher's
code to stderr. Use it only in the SPE, against synthetic data: a traceback
can contain data values. `result.json` is the same with or without it. The
hospital never passes `--debug`; the Agent, not the researcher, sets the
command.

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

## Pooling: `aggregate(results)`

```sh
run-analysis [--debug] --aggregate <code.py> <results.json> <result.json>
```

The orchestrator runs this once the hospitals have released their results (see
"Pooling the results" in `docs/federated-tasks.md`). `results.json` is a JSON
array with one item per hospital that released a result: the dict its
`site_analysis` returned. It holds no hospital names. The same file defines
both functions:

```python
def aggregate(results):  # a list of the dicts site_analysis returned
    n = sum(r["n"] for r in results)
    return {"n": n, "mean_age": sum(r["n"] * r["mean_age"] for r in results) / n}
```

The return value has the same rules as `site_analysis`'s (a dict, JSON, `n`,
61,440 bytes at most), and failures are reported the same way, with
`aggregate` in the message. The output has no `datasetSha256`. A
`results.json` that cannot be read or is not a list of dicts gives
`invalid_dataset`. Results are values, never files: for a graph, return the
data behind it (bins and counts, points) and draw it in the SPE.

Try `aggregate` in the SPE before you submit. `check-analysis` (see "In the
researcher's SPE") does this on one synthetic dataset, with its result given
twice. To try it on several, run `site_analysis` on two or more synthetic
datasets, put the dicts it returned in a JSON array, and run
the orchestrator's command on it:

```sh
run-analysis analysis.py synth_a/ /tmp/a.json   # repeat for each synthetic dataset
python3 -c 'import json,sys; print(json.dumps([json.load(open(f))["result"] for f in sys.argv[1:]]))' /tmp/a.json /tmp/b.json > /tmp/results.json
run-analysis --debug --aggregate analysis.py /tmp/results.json /tmp/pooled.json
```

`--debug` prints the full traceback, which the orchestrator never shows. At the
orchestrator, a failure is reported only as its code, plus the line of your file
for `code_error` (for example "aggregate() could not be imported, is missing, or
raised an exception (analysis.py line 7)").

## `schema.json`

```json
{"columns": [{"name": "age", "type": "integer"}, {"name": "sex", "type": "string"}]}
```

The types are `integer`, `number`, `string` and `boolean` (`true` or `false`,
any case). The pandas dtypes are `Int64`, `Float64`, `string` and `boolean`.
An empty cell is allowed in every column and becomes a missing value. An
`integer` is ASCII digits with an optional sign (`-12`, `+7`). A `number` is a
plain decimal with an optional exponent (`3`, `-1.5`, `.5`, `2E-3`). Spaces,
digit underscores (`1_000`), a trailing dot (`5.`), other digits and `nan` or
`inf` are not accepted. String cells may be as long as the file.

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
| `invalid_schema` | `schema.json` cannot be read, is not valid, has an unknown type or names a column twice. |
| `schema_mismatch` | The CSV columns are not the schema's, or a value is not of its column's type. |
| `code_error` | The researcher's file could not be imported, has no `site_analysis` (or `aggregate` with `--aggregate`), or the function raised an exception (also `sys.exit`). |
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
arguments, 1 when `result.json` could not be written. A failure of the wrapper
itself that it cannot report (for example running out of memory while parsing)
also ends without `result.json`; whatever runs the image treats a missing file
or a non-zero exit as a failure.

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
image must not keep or return the container's logs. The Agent's Docker runs use
the log driver `none`, so stdout and stderr are discarded, and the Agent reads
only `/outputs/result.json` (at most 65,536 bytes). The hospital operator must
do the same.

The Agent's Docker run also has no network, a read-only root file system, user
`1001:0`, all capabilities dropped, `no-new-privileges`, at most 256 processes,
swap off, and limits for CPU, memory and `/tmp` from the task and the TRE's
settings. An `fsize` ulimit caps every file the code writes at the `/tmp`
allowance. Many smaller files can still fill the host's disk through
`/outputs`, because a Docker volume has no size limit: production needs a
size-limited volume, or the operator's pod limits.

pip, uv, micropipenv, virtualenv, and the pip and setuptools wheels that
`ensurepip` uses are removed, so code cannot install packages. The list of
installed libraries is in `/opt/runner/libraries.txt` in the image.

Known PoC limit: the researcher's code runs in the same Python process as the
wrapper, so it can change the wrapper's behaviour, for example write
`result.json` itself. Output approval still sees the file before it leaves the
hospital.

## Try it

```sh
docker build --platform linux/amd64 -t python-runner:1 runner
docker run --rm --platform linux/amd64 --network none --read-only --tmpfs /tmp \
  -v "$PWD/runner/examples:/in:ro" -v "$PWD/out:/outputs" \
  python-runner:1 run-analysis /in/analysis.py /in/cohort /outputs/result.json
```

`out/` must exist and be writable by user 1001 (for example `chmod 0777 out`).

In the researcher's SPE, once `run-analysis` is installed in the notebook
image (PoC task 2.7; nothing installs it there yet), the same command works
against synthetic data:

```sh
run-analysis analysis.py ./synthetic/cohort ./result.json
```

## In the researcher's SPE

The package also has two commands and two templates for the researcher's SPE.
The workbench (PoC task 2.7) puts them there; the hospital image doesn't
install the commands.

- `templates/analysis.py` and `templates/notebook_analysis.ipynb` (in
  `src/run_analysis/`): what every SPE starts with. Comments explain the flow
  and what the two functions get and return. Both functions raise
  `NotImplementedError` until the researcher writes them; the notebook's path
  to the synthetic data, `SYNTHETIC_DATA_PATH`, is set by the workbench.
- `convert-notebook [--force] <notebook.ipynb> [<analysis.py>]` writes
  `analysis.py` (by default next to the notebook) from the notebook, the same
  bytes every time:
  - only code cells are kept; cells tagged `spe-only`, markdown cells, outputs
    and execution counts are left out;
  - `!` shell commands (also `x = !ls`), `%pip`, `%conda`, `%sx`, `%system`,
    `%%bash`, `%%sh`, `%%script`, `%%system` and any other cell magic refuse
    the notebook, naming the cell; `%%time`, `%%timeit` and `%%capture` keep
    their body; `%time`, `%timeit` and `%prun` keep the statement after them;
    other line magics are removed. As in Jupyter, a cell magic counts only on
    the first non-blank line of a cell (elsewhere it refuses the notebook), and
    `%` and `!` count only at the start of a logical line: inside a string,
    brackets or after a backslash they are Python and are kept;
  - line endings and trailing spaces are normalised, cells are separated by two
    blank lines, and the first line says the file was generated
    (`# Generated from <notebook> by convert-notebook. Edit the notebook, not this file.`);
  - it overwrites `analysis.py` only if the file is missing, was generated, or
    is still the untouched template; a hand-written file needs `--force`;
  - after writing, it runs the code checks of `check-analysis` and prints any
    problem (exit 1, the file stays written). It runs no code.
- `check-analysis [--dataset <dir>] [--timeout <seconds>] <analysis.py>` is
  what DAAMS runs before it submits (see `docs/federated-tasks.md`, "Before
  submission"). It checks that the code defines `site_analysis` and
  `aggregate` at the top level, is at most 262,144 bytes and isn't the
  unfilled template. With `--dataset`, it runs `site_analysis` on that
  synthetic dataset with `run-analysis`, then `aggregate` on the result given
  twice, each within the timeout (default 300 seconds). Exit 0 prints
  `analysis.py passed (...)` and `code_sha256 <hex>`; exit 1 prints one line
  saying what failed. A synthetic dataset that can't be read or doesn't match
  its schema skips the dry run (exit 0, `no dry run: ...`): it's the data's
  fault, not the code's.

```sh
convert-notebook notebook_analysis.ipynb
check-analysis --dataset ./synthetic/cohort analysis.py
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
write `/outputs`, Python is 3.11, code can neither open a network connection
nor change the dataset, and matplotlib, numpy, scipy and scikit-learn import
under a read-only root file system.

The image is amd64 only. On Apple silicon, Docker runs it under emulation.

## Base image

Today: `quay.io/opendatahub/odh-pipeline-runtime-datascience-cpu-py311-ubi9:2025a-v1.35`,
pinned by digest in the `Dockerfile`. It is the Open Data Hub data-science
runtime image, which has the notebook's libraries without Jupyter. It was
chosen before the SPE's image was known, and is a placeholder.

The SPE's notebook image is Open Data Hub 2025b with Python 3.12, which the
cluster offers as `s2i-generic-data-science-notebook:2025.2`:
`quay.io/opendatahub/odh-workbench-jupyter-datascience-cpu-py312-ubi9@sha256:ac55e1d640664318b4d6a5d4bf76557a7c9f2e805d91266e103e0e9459dbb4ca`
(`2025b-v1.40`). SPMS's build adds ACRO 1.1.0, installed with the base's
package versions as constraints, with SPMS's thresholds applied and its
folder made read-only.

To match it (still to do; code tasks run on the placeholder base until then):

1. Change `FROM` to the 2025b runtime image with Python 3.12:
   `quay.io/opendatahub/odh-pipeline-runtime-datascience-cpu-py312-ubi9@sha256:293512b01037b87ed095a56f1bebdc45ed1c6025ed55ff6f3517e59f95a16fa3`
   (`2025b-v1.40`).
2. Install `acro==1.1.0` the same way, apply the same SPMS `thresholds.yaml`,
   and make the ACRO folder read-only, before pip is removed.
3. Update the Python check in `runner/test.sh` to 3.12, check that the
   installer removal still covers the new image, add an image check that
   `import acro` works with SPMS's thresholds, and redo the library comparison
   below against the 2025b notebook image.

For the placeholder: the runtime image has the same data-science libraries as
the Open Data Hub 2025a notebook image (`odh-workbench-jupyter-datascience-cpu-py311-ubi9:2025a-v1.35`):
pandas 2.2.3, numpy 2.2.6, scipy 1.15.3, scikit-learn 1.6.1, matplotlib
3.10.5, pyarrow 21.0.0. statsmodels and seaborn are in neither. The packages
that differ are Jupyter and infrastructure packages (ipython, ipykernel,
pyzmq, kubernetes, protobuf, psutil, urllib3, click, opentelemetry-proto),
and onnx (1.19.0 here, 1.18.0 in the notebook).

Researcher code must use only these libraries. A package that the researcher
installs in the SPE notebook does not exist at the hospital.
