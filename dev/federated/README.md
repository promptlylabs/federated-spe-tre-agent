# Local federated task stack

A Docker Compose stack for trying the federated task flow on your machine:
queue a task in Submission.Api → the Agent polls it → runs the researcher's Python code on the CSV from the TRE's RustFS, in a container → saves the result as a JSON file in the TRE (`federated-outputs`) → registers it with the Data Egress app and waits → a reviewer approves or rejects it → on approval the Agent uploads it to the orchestrator's RustFS (`federated-results`).
See [docs/federated-tasks.md](../../docs/federated-tasks.md) for the flow itself.

For local development only. All passwords below are throwaway dev values.

## Start and stop

From the repo root:

```bash
docker compose -f dev/federated/docker-compose.yml up -d --build   # first run builds the APIs and the Egress app (a few minutes)
docker compose -f dev/federated/docker-compose.yml down            # stop (keeps data)
docker compose -f dev/federated/docker-compose.yml down -v         # stop and delete all data
```

On start, three one-shot steps prepare the test data:
- `seed-tre` registers the TREs `my-tre` and `other-tre` in Submission. `other-tre` has no Agent on this stack, so its part of a study version stays `queued` until the version ends: it shows a study sent to two hospitals, one of which has not answered yet. A study needs at least 2 hospitals, so every study here names both. With one Agent, no study on this stack is ready for aggregation: one `my-tre` rejects ends `incomplete` at the next sweep (every 15 s here), and one it releases stays `open` until its deadline (30 minutes here), then ends `incomplete`;
- `seed-dataset` uploads `data/cohort.csv` as `dataset/cohort_2024/cohort.csv`, and `data/schema.json` next to it (every task needs it);
- `seed-egress-creds` stores the `egress-app` login (realm `tre-test`) in the Egress app, so the app can send decisions back to the Agent.

If the stored login cannot be used (for example it was stored before the Egress app moved to `Shared/FiveSafesTes.Core`, whose encryption format is different, or with another `EncryptionSettings:Key`), the Egress API cannot send decisions to the Agent. Run the step again; it replaces the stored login:

```bash
docker compose -f dev/federated/docker-compose.yml up seed-egress-creds
```

The `python-runner` service only builds the runner image `python-runner:1` (from `runner/`) on your Docker daemon; its container exits at once. The image is amd64, so on Apple silicon it runs emulated and slowly.

Keycloak keeps no data: recreating its container (for example after a realm file changes) gives it new signing keys. Restart `agent-api`, `submission-api`, `egress-api` and `egress-ui` after that, or they reject the new tokens.

## What runs

| Service | URL on your machine | Login |
|---|---|---|
| Submission.Api (orchestrator) | http://localhost:5034 | Keycloak token, see below |
| Agent.Api (TRE Agent) | http://localhost:5269/hangfire (jobs dashboard) | `admin` / `password` |
| Data Egress UI (output review) | http://localhost:5280 | `reviewer` / `Reviewer1!` |
| Data Egress API | http://localhost:5279/swagger | Keycloak token (realm `Data-Egress`) |
| RustFS console (TRE storage) | http://localhost:9001/rustfs/console/ | access key `rustfsadmin` / `rustfsadmin123` |
| RustFS console (orchestrator storage) | http://localhost:9101/rustfs/console/ | access key `orchadmin` / `orchadmin123` |
| Keycloak | http://localhost:8180/admin | `admin` / `admin` |
| Postgres | localhost:55432 | `postgres` / `postgres` |

Keycloak users:

| Realm | User | Password | Role or group |
|---|---|---|---|
| `Dare-Control` | `control-admin` | `ControlAdmin1!` | `dare-control-admin`: can queue tasks |
| `Dare-Control` | `tre-agent-my-tre` | `TreAgent1!` | `dare-tre-admin`: a TRE human admin. Only for the negative test that it cannot call the Agent endpoints. The Agent uses the client `tre-my-tre`. |
| `Dare-Control` | client `tre-my-tre` | secret `dev-tre-my-tre-secret` | service account with `dare-tre-admin`: what the Agent uses to call Submission |
| `tre-test` | `uploader` | `uploader123` | group `dataset-upload`: RustFS console upload |
| `tre-test` | `egress-app` | `EgressApp1!` | `data-egress-admin`: the Egress app's login to the Agent, to send decisions (client `Dare-TRE-API`, secret `dev-dare-tre-api-secret`) |
| `Data-Egress` | `reviewer` | `Reviewer1!` | `data-egress-admin`: reviews results in the Egress app |
| `Data-Egress` | client `tre-agent` | secret `dev-tre-agent-egress-secret` | service account with `dare-tre-admin`: what the Agent uses to register results |

The Egress app's clients in `Data-Egress` are `Data-Egress-API` (secret `dev-data-egress-api-secret`, password grant, used by `task.sh`) and `Data-Egress-UI` (secret `dev-data-egress-ui-secret`). Emails to reviewers are switched off.

The `uploader` login through the RustFS console's "TRE Keycloak" button loops. That is a known RustFS console bug (rustfs/console#271), fixed upstream but not yet released. Use the access-key login for now.

## Try it

A task runs a Python file that defines `site_analysis(df)` (see `runner/README.md`) in a locked-down `python-runner:1` container. `task.sh` sends the file inline, with its SHA-256 as `fed.code_sha256`. `code/analysis.py` returns the number of people and their mean age.

Each `enqueue` (and `run`) creates a study version and prints its version ID; the commands that follow one hospital (`get`, `result`, `approve`, `reject`) take that hospital's task ID, which `part` prints. `cancel` takes the version ID. Every call is a new study (`study-<random>`), so runs never wait for each other's open version; set `STUDY_ID` to reuse one. The hospitals are `SITES`, `my-tre|other-tre` by default (Submission refuses fewer than 2):

```bash
v=$(./task.sh enqueue cohort_2024 code/analysis.py)   # prints the version ID
./task.sh part "$v"               # my-tre's task ID
./task.sh part "$v" other-tre     # other-tre's task ID
./task.sh study "$v"              # state open (no outcome yet), my-tre's and other-tre's phases
STUDY_ID=s1 ./task.sh enqueue cohort_2024 code/analysis.py   # twice in a row: the second gets 409 study_version_open
./task.sh cancel "$v"             # cancels the whole version: other-tre's part at once, my-tre's once its Agent confirms
```

```bash
cd dev/federated
./task.sh run cohort_2024 code/analysis.py      # RUNNING {"phase": "waiting_for_output_approval", ...}, then prints the approve/reject commands
./task.sh approve <task-id>                     # COMPLETE {"phase": "released", "result_key": "<treId>/<taskId>/<sha256>.json", "result_sha256": ...}
./task.sh result <task-id>                      # downloads and prints the result file: "result": {"kind": "analysis", "n": 3, "value": {"n": 3, "mean_age": 40.0}}
./task.sh run cohort_2024 code/analysis.py
./task.sh reject <task-id> "small cells"        # CANCELED {"phase": "rejected_at_output_approval", "error_code": "rejected_by_reviewer", "error_message": "small cells"}
./task.sh cancel <version-id>                   # CANCELED {"phase": "canceled", ...}: review closed, result removed from the TRE
./task.sh run nope code/analysis.py             # CANCELED, phase rejected_by_input_checks {"error_code": "dataset_not_found", ...}
./task.sh run cohort_2024 code/open_socket.py   # CANCELED, rejected_by_input_checks {"error_code": "unsafe_code", "error_message": "import-not-allowed line 1: ...", "scan_rules_version": "2026-11-2", ...}
```

`run` queues the task and waits until the Agent has saved the result and registered it for review (or rejected or failed the task). The Agent polls once a minute. A run spans several polls: one starts the container, later ones check it and collect `result.json`, so a task takes about two minutes to reach review. On the Hangfire dashboard, **Recurring jobs → Run Federated Tasks → Trigger now** runs it at once.

### Reviewing a result

Every result waits for a reviewer. Review it in one of two ways:

- **In the browser.** Open http://localhost:5280 and log in as `reviewer` / `Reviewer1!`. Open the request named `Federated task <task-id>`. The page shows the study ID, the code that ran and the result with its SHA-256. Set the file to Approved, or to Rejected and type a reason, then **Complete & Close Request**.
- **With `task.sh`.** `./task.sh approve <task-id>` or `./task.sh reject <task-id> "<reason>"` do the same through the Egress API as `reviewer`.

The Agent acts on the decision within seconds. `approve` and `reject` wait for it and print the final state. The SHA-256 on the review page is the `result_sha256` of the released file.

To see a changed result refused, overwrite a waiting task's saved result, then approve it:

```bash
echo '{"tampered":true}' | docker run --rm -i --network federated-dev_default -e AWS_ACCESS_KEY_ID=rustfsadmin -e AWS_SECRET_ACCESS_KEY=rustfsadmin123 \
  -e AWS_DEFAULT_REGION=us-east-1 amazon/aws-cli:2.15.0 --endpoint-url http://rustfs:9000 s3 cp - s3://federated-outputs/<task-id>.json
```

The task then ends as `SYSTEM_ERROR` with `result_changed_after_review`, and nothing is uploaded.

To test your own data, upload a CSV in the RustFS console to `federated-datasets/dataset/<name>/`, with exactly one `.csv` per folder, and a `schema.json` for it in the same folder (format in `runner/README.md`). Then run `./task.sh run <name> <file.py>`.

Where the result files are:
- **Orchestrator:** bucket `federated-results` on the second RustFS (console http://localhost:9101/rustfs/console/, `orchadmin` / `orchadmin123`), key `<treId>/<taskId>/<sha256>.json`. `task.sh result` reads it through Submission.
- **TRE:** bucket `federated-outputs` on the TRE RustFS (console http://localhost:9001/rustfs/console/, `rustfsadmin` / `rustfsadmin123`), key `<taskId>.json`. The review record is next to it, as `<taskId>.review.json`, with the registration marker `<taskId>.registered` (empty) once the Egress app has the task.

Logs: `docker compose -f dev/federated/docker-compose.yml logs -f agent-api` (and `egress-api`, `egress-ui`)

### Code runs

Code that raises ends as `EXECUTOR_ERROR`, `code_error`, with a fixed message (no data values). Code that runs longer than `MaxRunMinutes` (5 on this stack) ends as `EXECUTOR_ERROR`, `timeout`. `./task.sh cancel <version-id>` on a running task stops and removes its container.

The Agent starts the run containers on your Docker daemon, through the mounted `/var/run/docker.sock`; for that it runs as root on this stack (dev only). Each run's container and volume carry the label `fed.task-id` and are removed when the run ends. To check nothing is left:

```bash
docker ps -a --filter label=fed.task-id
docker volume ls --filter label=fed.task-id
```

## Demo

`demo.sh` runs four requests through both hospital gates, in plain language, for a recorded or live demo:

1. the hospital checks pass, a reviewer approves the result, and the hospital releases it to the orchestrator (the result is shown). The researcher does not get it: only results pooled from at least 2 hospitals go to researchers, and the study waits for a second hospital;
2. the request asks for a dataset the hospital does not hold, and its checks stop it;
3. R1: the code (`code/open_socket.py`) imports `socket` to send the data out; the hospital's code scan refuses it before the dataset is touched, and the demo prints the refusal with the rule and line;
4. the hospital checks pass, but the reviewer rejects the result, and nothing leaves.

It opens with the three promises the cases prove, and ends with a summary of what left the hospital.

```bash
cd dev/federated
./demo.sh                # live: pauses before each case, opens the review page, waits for the reviewer
./demo.sh --unattended   # no pauses: approves case 1 and rejects case 4 through the Egress API; exit 1 if a case ends unexpectedly
```

In the live run, approve case 1 and reject case 4 (with a reason) in the Egress app; the script notices the decision on its own and carries on. If the reviewer decides differently, the demo shows what really happened. If no decision reaches the hospital within 15 minutes (`DEMO_REVIEW_MINUTES` changes this), the case ends with a message naming the task. The demo shows real timing: the Agent picks up new requests once a minute, so a case can wait up to a minute, with a ticking timer, before the hospital checks it. Each case that runs code also spends about two minutes running it (under emulation on Apple silicon); cases 2 and 3 end at the checks.

### Before the demo

1. Start from a clean stack: `docker compose -f dev/federated/docker-compose.yml down -v`, then `up -d --build`, and wait until the seed steps have finished.
2. Run `./demo.sh --unattended` once to check everything works, then clean the stack again (step 1), so the Egress list shows only the demo's requests.
3. Log in to the Egress app (http://localhost:5280) as `reviewer` / `Reviewer1!` in the browser you will record, so the first review page does not stop at a login screen.
4. Arrange the screen: terminal on one side, that browser on the other; large terminal font.
5. Start recording, then run `./demo.sh`.

## Code-scan rules

Before the dataset is touched, the Agent scans the researcher's code with Semgrep and the hospital's rule file, and refuses code a rule finds (`rejected_by_input_checks`, `unsafe_code`, with the rule and line in the message). See "Code scan" in [docs/federated-tasks.md](../../docs/federated-tasks.md) for the outcomes and settings. The Agent image ships the default rules, `Agent/Agent.Api/ScanRules/default.yaml`, version `2026-11-2`. Each task records the version and SHA-256 of the rules it was checked with (`scan_rules_version`, `scan_rules_sha256` in `GetTask`). Suppression comments in the researcher's code (`# nosemgrep`, `# nosem`) are ignored, so code can't switch a rule off.

### What the default rules refuse

- **Imports not on the allowed list** (`import-not-allowed`). Allowed: `pandas`, `numpy`, `scipy`, `math`, `statistics`, `collections`, `itertools`, `functools`, `operator`, `datetime`, `decimal`, `fractions`, `re`, `json`, `typing`, `dataclasses`, `enum`, `warnings`, and their submodules. Anything else is refused, including what nobody thought of (`socket`, `os`, `subprocess`, `requests`, ...). `statsmodels` is not on the list because the runner image does not have it yet; add it once it does.
- **Code made from text, or names put together while it runs** (`dynamic-code`): `eval`, `exec`, `compile`, `__import__`, `importlib`, `globals`, `locals`, `vars`, `breakpoint`; `getattr`/`setattr`/`delattr` unless called directly with a written-out name (so `map(getattr, ...)`, `functools.reduce(getattr, ...)` and `g = getattr` are refused); attributes such as `__class__`, `__subclasses__`, `__globals__`; frame and code-object attributes such as `gi_frame`, `f_builtins`, `f_globals`, `tb_frame`; text (for example a formula or query) that mentions Python's internals, `os.`, `sys.`, `subprocess.` and similar, or calls `eval(`, `open(` and similar.
- **Starting programs or using the operating system** (`process-call`): `subprocess`, `os.system`, `os.popen`, `os.exec*`, `os.spawn*`, `fork`, `kill` and the like, also through `getattr` with the name written out.
- **Reading or writing files** (`file-access`): `open`, pandas `read_*` and the writers (`to_excel`, `to_parquet`, `to_pickle`, ...; `to_csv`, `to_json` and the like only when given a path), numpy `load`/`save`/`loadtxt`/`savetxt`/`genfromtxt`/`fromfile`/`tofile`/`dump`/`DataSource`, scipy `save_npz`/`load_npz`. The code gets its data as `df` and returns a dict; it never needs files.
- **Back doors in allowed packages** (`back-door`): `numpy.ctypeslib`, `numpy.f2py`, `numpy.distutils`, `numpy.testing`, `scipy.io`, `scipy._lib`, `pandas.testing`, `pandas.compat`, `pandas.io` (`get_handle`, `TextFileReader`), `pd.HDFStore`, `pd.ExcelWriter`, `np.memmap`, `typing.get_type_hints`, `runstring`, `pickle_compat`, `Unpickler`, `urlopen`; modules reached through an allowed package (`pd.io.common.os`, `.sys`, `.socket`, `.gzip`, `.codecs`, `dataclasses.inspect`, `warnings.linecache`, ...); `CodeType`/`FunctionType`; `operator.attrgetter`/`methodcaller` unless called directly with a written-out name, also under another name (`from operator import attrgetter as ag`); `df.query`, `df.eval` and `pd.eval` unless called directly with written-out text (`df.query("age > 40")` passes, `df.query(expr)` and `f = pd.eval` are refused).

- **An encoding other than UTF-8** (`encoding-not-utf8`): a `coding:` declaration on line 1 or 2 that is not UTF-8, for example `# -*- coding: utf-7 -*-`. Python would decode the file with it while Semgrep reads UTF-8, so code could hide in what the scan sees as a comment. The Agent checks this itself, before Semgrep, so it holds with any rule file.

### What they don't cover

Static analysis of Python can't catch every way out, so the scan is an early, explainable refusal, not the only barrier. Code that gets past it still runs in the locked-down container: no network, read-only root and `/data`, no capabilities. Known gaps of the default rules:

- Formula and query text built only from allowed-looking pieces. patsy formulas and `df.query`/`df.eval` with written-out text run as Python against the code's names; the text check catches the obvious routes, not every one.
- A dangerous module or object reached under a name the rules don't list, for example a module that an allowed package imports itself under another name. The rules refuse known names, not everything outside a known-safe set: an allowed package can hold other ways to files or Python's internals that nobody has listed yet.
- pandas methods called by name with a string through `agg`, `apply` or `transform`, for example `df.agg("to_" + "pickle", path)`.
- Pickle-style tricks through allowed libraries that nobody has listed yet.

And some safe code they refuse (false positives) a researcher may hit:

- a column read as an attribute with a listed name, such as `df.os` or `df.sys` (use `df["os"]`);
- text that happens to contain `open(` or `x.os.`;
- `json.load` (the rule refuses any `load`; `json.loads` passes);
- `super().__init__()` and other names in double underscores (only `__name__` itself is allowed).

### A hospital's own rule file

A hospital may replace the default with its own Semgrep YAML file. Mount it into the Agent container and point `FederatedTaskSettings:ScanRulesPath` at it, for example in Compose:

```yaml
  agent-api:
    volumes:
      - ./hospital-rules.yaml:/rules/hospital-rules.yaml:ro
    environment:
      FederatedTaskSettings__ScanRulesPath: /rules/hospital-rules.yaml
```

- Keep `# version: <text>` as the first line (at most 64 characters), and change it whenever you change the file. The Agent records it with each task, next to the file's SHA-256. Without the line, or if the file is missing or unreadable, the Agent holds every task and logs an error; it does not reject them.
- Give every rule an `id` and a `message` a data custodian can read: the refusal shows `<id> line <n>: <message>`. An `id` must not contain `.`: the Agent strips the path prefix Semgrep adds to it at the last dot.
- To allow another import, add it to the list in `import-not-allowed`. It must exist in the runner image (`runner/list-libraries.py` lists what is there).
- A file Semgrep can't load (a YAML or rule error) also holds every task, and the Agent logs Semgrep's error; test it first (below).
- The same code may then pass at one hospital and be refused at another.

### Testing the rules

`Agent/Agent.Api/ScanRules/test.sh` runs Semgrep's rule tests: every `# ruleid: <id>` line in `default.py` must be found by that rule, and every `# ok: <id>` line must not. It runs the Semgrep version pinned in `requirements.txt` (the one the Agent image installs), in the `semgrep/semgrep` container with no network:

```bash
Agent/Agent.Api/ScanRules/test.sh   # ends with "✓ All tests passed"
```

Run it after every change to `default.yaml`, and add a `# ruleid:` or `# ok:` line to `default.py` for each new case. For a hospital's own file, run the same command over that file and its own fixture:

```bash
docker run --rm --network none -v "$PWD:/rules:ro" -w /rules semgrep/semgrep:1.185.0 \
  semgrep --test --metrics=off --disable-version-check --config hospital-rules.yaml hospital-rules.py
```

### Open point: how hospitals get the Agent image

The Agent image installs Python and a pinned Semgrep (`ScanRules/requirements.txt`, with hashes) from PyPI at build time; nothing is downloaded at run time. That makes the image about 980 MB instead of about 420 MB. How hospitals obtain or build the Agent image is not known yet. If their build can't reach PyPI, or must use a UBI base, the install method changes (for example copying Semgrep from a pinned image, or vendoring the wheel).

## Integration tests

`Tests/Federated.IntegrationTests` drives the real loop on this stack (Submission → Agent → Egress app) and checks how each task ends in `GetTask`, and that only a released task has a result file in the orchestrator's result store (RustFS on port 9100). Every task it queues is a code task in `python-runner:1`, with the small `site_analysis` files in `Code.cs`. It covers the three demo cases, cancelling, study versions across `my-tre` and `other-tre`, and a set of deliberately invalid tasks. Every study names `my-tre` and `other-tre`. The study deadline (timeout) is covered by unit tests only, because the stack's 30 minutes protects the live demo:

| Gate | Case | Expected end |
|---|---|---|
| Hospital input checks | dataset the hospital does not hold | `CANCELED`, `rejected_by_input_checks`, `dataset_not_found` |
| Hospital input checks | a dataset folder with two `.csv` files | `CANCELED`, `rejected_by_input_checks`, `ambiguous_dataset` |
| Hospital input checks (code scan, R1) | code that imports `socket`, on a dataset the hospital holds | `CANCELED`, `rejected_by_input_checks`, `unsafe_code` naming `import-not-allowed line 1`; `scan_rules_version` `2026-11-2`; no result |
| Submission's task profile | a count task in the shape used before every task ran code, an image Submission does not accept, the old tag names, code that does not match `fed.code_sha256` | Enqueue refused with 400 `invalid_task`; no task |
| Output approval | reviewer approves | `COMPLETE`, `released`, result stored with the reviewed SHA-256 |
| Output approval | reviewer rejects | `CANCELED`, `rejected_at_output_approval`, `rejected_by_reviewer`, the reason; no result |
| Output approval | result overwritten after it was registered, then approved | `SYSTEM_ERROR`, `result_changed_after_review`; no result |
| Cancel | a queued task, a running task, a task waiting for review, a cancel at the same time as an approval | `CANCELED`, `canceled`; no review left open, no result in the TRE or the orchestrator |
| Study versions | a version for `my-tre` and `other-tre`: `my-tre` released, `other-tre` queued | version `open`; a second submission gets 409 `study_version_open`; cancelling ends it (`other-tre` `canceled`) and version 2 is accepted |
| Study versions | cancel while `my-tre` waits for review and `other-tre` is queued | version `canceling`, a new version accepted at once; `ended` once `my-tre` confirms |
| Study versions | a study naming only `my-tre` | Enqueue refused with 400 `invalid_task` |
| Study outcome (R2) | `my-tre`'s reviewer rejects, `other-tre` is queued | at the next sweep: outcome `incomplete`, `minimum_unreachable`; `other-tre` `canceled`; version `ended` and a new version accepted |

"Image not approved" cannot happen on this stack (Submission accepts only `python-runner:1`, and the Agent approves it); unit tests cover it.

With the stack running, from the repo root:

```bash
docker run --rm -v "$PWD":/src -w /src -e FEDERATED_STACK_HOST=host.docker.internal \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test Tests/Federated.IntegrationTests
```

(With a local .NET SDK: `dotnet test Tests/Federated.IntegrationTests`.) The tests trigger the Agent's poll through the Hangfire dashboard, so they take seconds, not minutes. When the stack is not running, every stack test is skipped, with a message saying how to start it. When it runs but a service, login or seed step is broken, every stack test fails and says what is wrong. Run them before merging changes to the federated loop.

The tests delete the dataset folders they create, but their tasks and Egress requests stay. Start the demo from a clean stack (see "Before the demo").

## Checking custodian access (known dev limitation)

Members of the Keycloak group `federated-outputs-read` can read result files through the S3 API with STS. In this stack the tokens say `iss = http://localhost:8180/realms/tre-test`, but RustFS discovers `http://keycloak:8080/realms/tre-test`. STS then fails with "no OIDC provider configured for issuer". To check it anyway, set the issuer by hand. These changes live only in the running Keycloak and are lost when its container is recreated.

```bash
K=http://localhost:8180; A=$K/admin/realms/tre-test
AT=$(curl -sf $K/realms/master/protocol/openid-connect/token -d grant_type=password -d client_id=admin-cli -d username=admin -d password=admin | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
H="Authorization: Bearer $AT"
# 1. Temporarily make tre-test tokens carry the issuer RustFS expects
curl -s $A -H "$H" | python3 -c 'import sys,json;d=json.load(sys.stdin);d.setdefault("attributes",{})["frontendUrl"]="http://keycloak:8080";print(json.dumps(d))' > realm.json
curl -X PUT $A -H "$H" -H 'Content-Type: application/json' -d @realm.json
# 2. Create the group and a user in it
curl -X POST $A/groups -H "$H" -H 'Content-Type: application/json' -d '{"name":"federated-outputs-read"}'
curl -X POST $A/users -H "$H" -H 'Content-Type: application/json' -d '{"username":"custodian","enabled":true,"email":"custodian@example.org","emailVerified":true,"firstName":"Cus","lastName":"Todian","credentials":[{"type":"password","value":"custodian123","temporary":false}]}'
USR=$(curl -s "$A/users?username=custodian&exact=true" -H "$H" | python3 -c 'import sys,json;print(json.load(sys.stdin)[0]["id"])')
GID=$(curl -s "$A/groups?search=federated-outputs-read" -H "$H" | python3 -c 'import sys,json;print(json.load(sys.stdin)[0]["id"])')
curl -X PUT $A/users/$USR/groups/$GID -H "$H"
# 3. Allow the password grant on the rustfs-console client (it is off in the realm file)
CID=$(curl -s "$A/clients?clientId=rustfs-console" -H "$H" | python3 -c 'import sys,json;print(json.load(sys.stdin)[0]["id"])')
curl -s $A/clients/$CID -H "$H" | python3 -c 'import sys,json;d=json.load(sys.stdin);d["directAccessGrantsEnabled"]=True;print(json.dumps(d))' > client.json
curl -X PUT $A/clients/$CID -H "$H" -H 'Content-Type: application/json' -d @client.json
# 4. Token, then STS credentials
TOKEN=$(curl -s $K/realms/tre-test/protocol/openid-connect/token -d grant_type=password -d client_id=rustfs-console -d client_secret=rustfs-secret -d username=custodian -d password=custodian123 -d scope=openid | python3 -c 'import sys,json;print(json.load(sys.stdin)["id_token"])')
docker run --rm --network federated-dev_default -e AWS_DEFAULT_REGION=us-east-1 amazon/aws-cli:2.15.0 --endpoint-url http://rustfs:9000 \
  sts assume-role-with-web-identity --role-arn arn:aws:iam::rustfs:role/x --role-session-name sess1 --web-identity-token "$TOKEN" --no-sign-request
# 5. Use the returned keys (and session token) with `aws s3 ls s3://federated-outputs`, then revert step 1
curl -s $A -H "$H" | python3 -c 'import sys,json;d=json.load(sys.stdin);d["attributes"].pop("frontendUrl",None);print(json.dumps(d))' > realm.json
curl -X PUT $A -H "$H" -H 'Content-Type: application/json' -d @realm.json
```

Expected: listing and reading `federated-outputs` work; upload, delete and listing `federated-datasets/dataset/` give AccessDenied.

## Not included

RabbitMQ, Vault, Zeebe, LDAP and the Agent's other jobs (sync, scan, health check, bucket cleanup) are left out. The federated flow does not need them. Agent.Api logs connection errors for RabbitMQ and Vault at startup; these are expected.
