# Node contract: what a hospital node must do to join the federated SPE

| | |
|---|---|
| Status | Draft, 2026-11-27 (ANZE-6134) |
| Owner | Rita Peixoto |
| For | Hospitals that build their own node, SPMS (to put the obligations in each hospital's agreement), and reviewers |

A **node** is the software at a hospital that takes the hospital's part of a study, runs it on the hospital's data, has the hospital's reviewer approve the result, and reports back to the orchestrator. Our Agent is the reference implementation (section 7). A hospital can run our Agent, or build its own node against this contract.

The words **must**, **must not** and **may** are requirements. `docs/federated-tasks.md` has the full detail of each route and format; this document says what a node has to do with them.

## 1. The protocol in one line

A node fetches its tasks with the standard GA4GH TES 1.1 `ListTasks` call, receives TES tasks with TES states and the `fed.*` tags, and reports progress and results with two calls of our own. TES has no operation for a node to report back, so those two calls are this profile's extension.

What to claim, and what not: **a standard TES API for submitters, and the TES task format and states for nodes**. Not "TES end to end".

## 2. Identity

1. A node logs in to Keycloak (realm `Dare-Control`) with its own client, using the client-credentials grant. The client's service account has the role `dare-tre-admin`.
2. The orchestrator knows the node by that client: each registered hospital (TRE) has one Keycloak client. The node's identity comes from its token, never from a request's content.
3. A node must keep its client credentials secret. Whoever holds them acts as the hospital.
4. Planned: each node proves its identity with a key pair instead of a client secret, and signs its results (see section 8).

## 3. Fetching work

| Call | What the node gets |
|---|---|
| `GET /v1/tasks?view=FULL` | Its unfinished parts as a TES `ListTasks` response (`{"tasks": [...]}`), most urgent first: parts to cancel, then running, then queued, then waiting for review. At most 50; no paging. Never another hospital's parts. |
| `GET /v1/tasks/{id}` | One of its own parts. Anything else: 404 `task_not_found`. |

1. A node must poll regularly (our Agent: every minute) and act on every part it gets.
2. A node must handle a part to cancel (`state` `CANCELING`, metadata `phase` `canceling`) before it starts new work.
3. Each part is a TES task: `state` is a TES state, and `logs[0].metadata.phase` is the phase (section 5).

## 4. The task

Every part follows the federated TES profile (`docs/federated-tasks.md`, "The federated TES profile"):

- **Executor**: one image, and exactly the command `["run-analysis", "/code/analysis.py", "/data/<dataset>", "/outputs/result.json"]`.
- **Inputs**: the researcher's code inline at `/code/analysis.py`, and the dataset as `dataset://<name>`, which the node resolves to its own copy.
- **Output**: `/outputs/result.json` to `result://`.
- **Tags**: `fed.sites` (this hospital only), `fed.study_id`, `fed.code_sha256`, optional `fed.round` (always 1) and `fed.request_id`.

## 5. Reporting

Progress: `POST /api/FederatedTask/ReportProgress` with `{ "task_id": "...", "phase": "..." }`.

| Phase the node reports | When | TES state |
|---|---|---|
| `running` | It picked a queued part up | `RUNNING` |
| `waiting_for_output_approval` | The result is registered for its reviewer | `RUNNING` |
| `canceled` | It finished cleaning up a part to cancel | `CANCELED` |

The final report: `POST /api/FederatedTask/ReportResult` (multipart), with the TES state of the outcome:

| State | Meaning | Phase it leads to |
|---|---|---|
| `COMPLETE`, with the result file | The reviewer approved; these are the approved bytes | `released` |
| `CANCELED`, with `RejectedAt` `input_checks` or `output_approval` | The node refused the task, or its reviewer rejected the result | `rejected_by_input_checks` or `rejected_at_output_approval` |
| `EXECUTOR_ERROR` | The researcher's code failed | `failed` |
| `SYSTEM_ERROR` | The node failed | `failed` |

Error codes and messages come from the fixed list in `docs/federated-tasks.md` ("Error codes"). A node must never put text produced by the researcher's code in a report: that text can hold data values.

## 6. Obligations

### Before anything runs

1. The node must check the part against the profile (section 4), and that the SHA-256 of the code equals `fed.code_sha256`.
2. The node must run only images on its own allow-list (`image_not_approved` otherwise).
3. The node must scan the code with its own rules, and refuse code they flag (`unsafe_code`).
4. The node must check that the dataset exists, with a `schema.json`, before the run.

### The run

5. The code must run isolated: **no network**, a **read-only file system** except a scratch `/tmp` and the output folder `/outputs`, an **unprivileged user**, **no package installers**, and **limits** on CPU, memory, disk and time.
6. The node must **not keep** the run's standard output and error. Researcher code can print data values.
7. The run must produce only `result.json` in the result document format (`docs/federated-tasks.md`, "Result document"): a small JSON object with `n`, at most 65,536 bytes.

### Review and release

8. A person at the hospital must approve every result before it leaves the node. The reviewer must see the researcher's code (including `aggregate()`, which combines the result with the other hospitals' results) and the exact result.
9. The node must release only the exact bytes the reviewer approved, and must report a rejection, with the reviewer's reason, as `CANCELED` at `output_approval`.
10. Nothing is withheld automatically at the node: small numbers are checked after pooling, at the orchestrator (`docs/proposals/small-numbers-at-aggregation.md`). The reviewer is the node's protection against patient-level data in a result.

### Cancellation and clean-up

11. For a part to cancel, the node must stop the run, withdraw the review, delete the result, and then report `canceled`.
12. The node must delete the staged dataset copy and the run's files when a part ends, released, rejected, failed or cancelled.

### Evidence

13. The node must keep, for at least one year, a record of each part it processed: the task ID, the code's SHA-256, the dataset's SHA-256, the scan rules version, the times of each phase, the reviewer's decision and the result's SHA-256, and the deletion of staged data. Each entry names who or what acted.

### Where the run happens

14. A node may run the code with any technology (Docker, Kubernetes, SPMS's SPE operator, or its own TES server such as Funnel or TESK) **if** obligations 5 to 7 and 12 hold. With a TES server, the hospital must configure it for isolation and must turn off the keeping of executor logs: TES servers keep them by default.
15. The hospital is responsible for these obligations on its own infrastructure. The hospital's agreement with SPMS states them.

## 7. The reference implementation: our Agent

Our Agent (`Agent/Agent.Api`) does all of the above, except that its record of deleting staged data (obligation 13) is only in its own log for now; sending it to the orchestrator's audit trail is planned (section 8):

| Obligation | Where |
|---|---|
| Fetch and report (sections 3 and 5) | `FederatedTaskClient` |
| Profile and hash (1), images (2), code scan (3), dataset (4) | `DoFederatedTaskWork`, `SemgrepCodeScanner`, `FederatedInputChecks` |
| Isolated run (5), no logs kept (6), result format (7) | `DockerCodeRunner` and the `python-runner:1` image (`runner/README.md`); in production, SPMS's SPE operator starts the same image with the same rules |
| Review (8, 9) | Registration with the hospital's Egress app; the reviewer's decision bound to the result's SHA-256 |
| Cancellation and clean-up (11, 12) | `DoFederatedTaskWork`, `CodeExecution` |
| Evidence (13) | The phase history and result document at the orchestrator; the Agent's log |

The Agent runs the code itself (Docker) or through the SPE operator. It cannot yet hand the run to a hospital's own TES server; that would be an adapter added when a node that runs one joins.

## 8. Planned changes to this contract

- The node proves its identity with a key pair, verifies the orchestrator's signature on each task before running it, and signs its result.
- The node encrypts its result with crypt4GH to the aggregator's key, so only the aggregator can read it.
- A record of the deletion of staged data, with hashes and times, is sent to the orchestrator's audit trail.
