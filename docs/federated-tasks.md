# Federated tasks

The orchestrator (Submission.Api) takes a GA4GH TES task for a study and sends it to each hospital (TRE) the task names. The TRE Agent (Agent.Api) at each hospital polls for its part, runs the researcher's Python code on a CSV dataset from the TRE bucket, in a locked-down container, holds the result until a hospital reviewer approves it in the Data Egress app, and then reports the result.

There is no DAAMS signing or approval gate on the orchestrator side yet.

To try the flow locally, see [dev/federated](../dev/federated/README.md). It is a Docker Compose stack with both APIs, the Data Egress app, Keycloak, RustFS and a sample dataset.

## Studies and versions

- A **study** is `fed.study_id`. It collects versions over time.
- A **study version** is one submission of code to the study's hospitals (`fed.sites`). Submission numbers versions 1, 2, 3… per study, and gives each a version ID. `Enqueue` returns the version ID.
- A **hospital's part** of a version is a federated task, with its own task ID. Everything under "Flow" below happens per part: input checks, run, output approval, release or refusal, cancel cleanup. Each hospital sees and reports on only its own part.

Fan-out. Every hospital gets a copy of the submitted task: the same code byte for byte, the same `fed.code_sha256` and `fed.study_id`, and the same dataset name, which each hospital resolves to its own copy. Only its `fed.sites` differs: it names that hospital alone. The version keeps the task as submitted. If any hospital in `fed.sites` is not registered, the whole submission is refused and nothing is stored, so no hospital starts.

At least 2 hospitals. A study is pooled only from at least 2 hospitals, so that no single hospital's result is ever released. Intake refuses `fed.sites` naming fewer than 2 (400 `invalid_task`), so a study that could never meet the minimum does not use hospitals' compute and reviewers' time.

Outcome. Every version ends with one outcome:

| Outcome | Reason | When |
|---|---|---|
| `ready_for_aggregation` | `all_final` | Every part is final and at least 2 are `released`. |
| `ready_for_aggregation` | `timeout` | The deadline passed with at least 2 `released`. The parts still working are cancelled. |
| `incomplete` | `minimum_unreachable` | Fewer than 2 parts are `released` or can still be. This is decided at once, not at the deadline: for example, in a study for A, B and C where A and B are rejected, C is cancelled. |
| `incomplete` | `timeout_below_minimum` | The deadline passed with fewer than 2 `released`. The parts still working are cancelled. |
| `canceled` | `canceled` | An orchestrator admin cancelled the version (see "Cancelling a study version"). |
| `aggregated` | (the ready reason) | The aggregator ran the researcher's `aggregate()` and the pooled result is stored (see "Pooling the results"). |
| `failed` | (the ready reason) | Pooling failed; `aggregation_error_code` says why. No pooled result. |

A version is `ready_for_aggregation` first, then `aggregated` or `failed`. The only change back is an admin's `RetryAggregation` (see "Pooling the results").

At least one `aggregate`. Intake also refuses code with no top-level `def aggregate(` line (400 `invalid_task`): the orchestrator pools the hospitals' results with it, so without it every hospital would run and review the study for nothing. Code that defines it some other way still fails `code_error` at the aggregator.

Only `released` counts. `rejected_by_input_checks`, `rejected_at_output_approval`, `failed` and `canceled` do not. An `incomplete` version releases nothing to researchers; researchers will only ever get pooled results. Admins can still read a hospital's released result with `GetTaskResult`. The outcome also has a sentence for people, `outcome_message`, for example "only 1 hospital(s) released or can still release a result; a study needs at least 2".

Deadline. Each version's deadline is its submission time plus `FederatedTaskSettings:StudyTimeoutMinutes` (default 7 days), fixed at intake: changing the setting does not move the deadline of versions already submitted. The deadline does not limit a run (the Agent's `MaxRunMinutes` does that). It limits how long one hospital, a reviewer who does not answer or an Agent that is gone, can hold up the others.

The sweep. A hospital that goes silent sends nothing, so Submission decides outcomes on a timer: at startup, then every `FederatedTaskSettings:StudySweepSeconds` (default 300). It decides each version that has no outcome yet, and saves each on its own. When it decides, it cancels every unfinished part as an admin cancel does (`queued` → `canceled`, picked up → `canceling` until the hospital confirms). If a hospital's report changes a part at the same moment, whichever saves first wins: if the report does, the sweep's save (outcome and cancels together) is rolled back and the next sweep decides again from the new phase; if the sweep does, the report gets 409 `task_canceling` and its result is never released. So an outcome can show up to one interval after the last hospital finished.

One open version per study. A version is `open` until it has an outcome. Then it is `canceling` while a part is still `canceling` at its hospital, and `ended` once every part is final. Only an open version blocks: a submission for a study whose latest version is open gets 409 `study_version_open`, with the open version's ID. A decided version whose hospitals are still cleaning up does not block, and their cleanup carries on in the background. Two submissions for one study at the same moment cannot both get a version: the version number is unique per study, so the loser reads the study again and gets 409.

A decided version stops blocking at once because a hospital whose Agent is gone for good would otherwise block the study forever. It is safe: a canceling part can never release a result. At the hospital, each poll hands out canceling parts before queued ones, and the cleanup stops the old run first, so the old and new versions' code do not run together unless that cleanup fails partway (then the hospital's `MaxConcurrentRuns` and `MaxRunMinutes` bound it).

## Flow

1. A `dare-control-admin` user calls `POST /api/FederatedTask/Enqueue` with a TES task. Submission validates it against the federated profile and creates a study version with one task per hospital (see "Studies and versions"), each at the phase `queued`. The response is `{ "id": "<versionId>" }`; `GetStudyVersion` gives each hospital's task ID.
2. The Agent job `Run Federated Tasks` (`JobSettings:FederatedTaskJobName`) runs every `JobSettings:federatedTaskSchedule` minutes. It calls `GET /api/FederatedTask/GetPendingTasksForTre`. The TRE is taken from the token.
3. The Agent checks the task against the profile, then runs the hospital's input checks in this order: the executor image is in `ApprovedImages` (see "Approved images"); the code passes the hospital's code scan (see "Code scan"); the one `.csv` under `dataset/<name>/` is in the dataset bucket; and a `schema.json` is in the same folder. The first failure wins. The scan comes before the dataset, so code the hospital refuses never touches the dataset bucket. It reads no data values yet. If a check fails, or there is no file or more than one, the task is rejected (`rejected_by_input_checks`). If `ApprovedImages` is not set, the task still gets the profile check, but the hospital's checks do not run. The task is not run or reported, and keeps its phase. A `queued` task rejected here never reaches `running`. A resumed `running` task is checked again, so if its dataset was removed in the meantime it is rejected with a `running_at` in its history. If the dataset bucket cannot be listed, the task keeps its phase and is tried again at the next poll, until `DatasetUnreachableGiveUpMinutes` (default 60) have passed since it was queued, or since the Agent started if that is later. A task that waited while the Agent was down, or while `ApprovedImages` was not set, so gets the full time. Each restart of the Agent gives pending tasks the full time again. After that it fails with `SYSTEM_ERROR` and `internal_error`, so a bucket that is wrongly configured or denied does not leave tasks queued for good.
4. For a `queued` task, the Agent calls `POST /api/FederatedTask/ReportProgress` with the phase `running`. Submission moves the task to `running`. Then the Agent reads the CSV and `schema.json` to stage them for the run. If the bucket fails during the read, or Docker has a fault that may pass by itself (the daemon is down or restarting, a server error, a broken socket or a timeout), the task stays `running` and is resumed at the next poll, with the same `DatasetUnreachableGiveUpMinutes` limit as a failed listing (for a Docker fault, counted from the first fault, so a task that waited long in the queue still gets the whole time). A finished run is kept meanwhile, so its result is collected once Docker is back. A Docker error that waiting can't fix (for example an image that doesn't exist) is reported at once (`internal_error`). A finished run is kept meanwhile, so its result is collected once Docker is back. A Docker error that waiting can't fix (for example an image that doesn't exist) is reported at once (`internal_error`).
5. The code runs in a container (see "Running a code task"). The Agent starts it and checks it again at each poll, so a poll never waits for the code. The runner checks the CSV against `schema.json`; a file that is not valid CSV fails with `invalid_dataset`. The result is a JSON file (see "Task state and result").
6. The Agent saves the file in the TRE bucket `federated-outputs` as `<taskId>.json`. If the file is already there from an earlier poll, the Agent reuses it and does not compute again.
7. The Agent registers the result with the Data Egress app, then calls `ReportProgress` with the phase `waiting_for_output_approval`. The task stays `RUNNING`. Nothing has left the TRE. See "Output approval".
8. A hospital reviewer opens the request in the Egress app. The page shows the study ID, the code that ran (image and command) and the result in full, with its SHA-256. The reviewer approves, or rejects with a reason. The Egress app sends the decision to the Agent, which records it in `federated-outputs/<taskId>.review.json`.
9. Right after recording the decision, the Agent acts on it in a background job. The poll acts on a recorded decision too, in case the job could not. On approval it hashes the saved file again. If the hash equals the reviewed SHA-256, it releases the file: `POST /api/FederatedTask/ReportResult` with the file as an upload. Submission checks the caller and the file, and stores it in the orchestrator bucket `federated-results`. The task moves to `released`. On rejection the Agent reports `CANCELED` with `RejectedAt` `output_approval`, the code `rejected_by_reviewer` and the reviewer's reason as the message, and no file. If the saved file no longer matches the reviewed one, or is gone, the Agent reports `SYSTEM_ERROR` with `result_changed_after_review` and no file.
10. After a release, the Agent compares the SHA-256 that Submission stored with the SHA-256 of its own copy. If they match, it adds the metadata `released-key` and `released-sha256` to the TRE copy.
11. A `dare-control-admin` user calls `GET /api/FederatedTask/GetStudyVersion?id=<versionId>` (or `GetStudy`) for the version and each hospital's phase, `GET /api/FederatedTask/GetTask?id=<taskId>` for one hospital's phase, state and where its result is stored, and `GET /api/FederatedTask/GetTaskResult?id=<taskId>` for that hospital's file.

If the Agent cannot save the file, or the registration with the Egress app fails, or the release fails (network error, 5xx, or a 4xx that is not listed under "Reporting a result"), the task stays `running` and the Agent tries again at the next poll. A task that fails before there is a file (for example `dataset_not_found` or `timeout`) is reported as a rejection or a failure, with no file, and does not go through save and release.

`GetPendingTasksForTre` returns the tasks that are not finished: `canceling` first, then `running`, then `queued`, then `waiting_for_output_approval`, oldest first inside each group. For a `waiting_for_output_approval` task the Agent reads the review record and acts on the decision, if there is one (see "Output approval"). A `running` task was marked by an earlier poll that did not finish. The Agent resumes it, and does not report `running` again. If the result file from the earlier poll is in the TRE bucket, the Agent reuses it. Otherwise it runs the task again from the start, so every step the Agent does before it reports must be safe to do twice. The Agent skips, and logs, a pending task in any state other than `QUEUED` or `RUNNING`. A `RUNNING` task is either `running` or `waiting_for_output_approval`; the Agent reads the phase to tell which.

`GetPendingTasksForTre`, `ReportProgress` and `ReportResult` accept only the TRE Agent's service account (role `dare-tre-admin`). See "Reporting a result". `Enqueue`, `GetStudy`, `GetStudyVersion`, `Cancel`, `GetTask` and `GetTaskResult` need `dare-control-admin`.

```mermaid
sequenceDiagram
    autonumber
    actor Admin as Orchestrator admin
    participant Sub as Submission.Api
    participant OS as Orchestrator RustFS<br/>federated-results
    box TRE and hospital (only outbound calls leave)
        participant Agent as Agent.Api
        participant TS as TRE RustFS<br/>federated-datasets / federated-outputs
        participant Egress as Data Egress app
        actor Reviewer as Hospital reviewer
    end

    Admin->>Sub: POST Enqueue (TES task)
    Sub-->>Admin: version id (one task per hospital, phase queued)
    loop every federatedTaskSchedule minutes
        Agent->>Sub: GET GetPendingTasksForTre (TRE from token)
        Sub-->>Agent: running, queued and waiting tasks for this TRE
    end
    Agent->>Agent: check the task against the profile
    Agent->>TS: find the dataset CSV (no data read)
    alt check fails, or no file or more than one
        Agent->>Sub: POST ReportResult (CANCELED, RejectedAt input_checks, code + message)
        Note over Agent,Sub: rejected_by_input_checks; a queued task never reaches running
    end
    Agent->>Sub: POST ReportProgress (phase running), only for a queued task
    Sub-->>Agent: 200, task is running (409 if the task is past running)
    Agent->>TS: read dataset CSV and schema.json
    Agent->>Agent: run the code in a container, build result JSON
    alt success
        Agent->>TS: save federated-outputs/taskId.json (once, reused on retry)
        Agent->>TS: save taskId.review.json (pending, result sha256)
        Agent->>Egress: POST AddNewDataEgress (study, code, result bytes, sha256)
        Agent->>TS: save taskId.registered (empty marker)
        Agent->>Sub: POST ReportProgress (phase waiting_for_output_approval)
        Reviewer->>Egress: approve, or reject with a reason
        Egress->>Agent: POST EgressResults (decision, reviewer, time, reason, sha256)
        Agent->>TS: save decision in taskId.review.json
        Note over Agent: background job, at once
        alt rejected
            Agent->>Sub: POST ReportResult (CANCELED, RejectedAt output_approval, rejected_by_reviewer, reason, no file)
        else approved, saved file no longer matches the reviewed sha256
            Agent->>Sub: POST ReportResult (SYSTEM_ERROR, result_changed_after_review, no file)
        end
        Agent->>Sub: POST ReportResult (state + result file upload), only when approved and unchanged
        Sub->>Sub: check caller is this TRE's Agent service account
        Sub->>Sub: check owner + not final, validate file, hash
        alt file rejected (400 invalid_result / result_too_large)
            Sub-->>Agent: 400, nothing stored
            Agent->>Sub: POST ReportResult (EXECUTOR_ERROR, same code, no file)
        else already reported (409)
            Sub-->>Agent: 409 task_already_reported (+ result_sha256 and result_key if released)
            Agent->>Agent: check result_sha256 equals own sha256
            Agent->>TS: tag TRE copy with released-key / released-sha256
        else storage write fails
            Sub->>OS: put treId/taskId/sha256.json
            Sub-->>Agent: 503 storage_unavailable, task keeps its phase, retried next poll
        else stored
            Sub->>OS: put treId/taskId/sha256.json
            Sub->>Sub: save phase released + bucket/key/sha256
            Sub-->>Agent: 200 + result_key/result_sha256
            Agent->>Agent: check result_sha256 equals own sha256
            Agent->>TS: tag TRE copy with released-key / released-sha256
        end
    else input check fails (e.g. dataset_not_found)
        Agent->>Sub: POST ReportResult (CANCELED, RejectedAt input_checks, error_code, error_message, no file)
        Sub-->>Agent: 200, phase rejected_by_input_checks, no file written
    else task fails (e.g. code_error)
        Agent->>Sub: POST ReportResult (EXECUTOR_ERROR or SYSTEM_ERROR, error_code, no file)
        Sub-->>Agent: 200, phase failed, no file written
    end
    Admin->>Sub: GET GetTask
    Sub-->>Admin: state + phase + phase times + result_bucket/result_key/result_sha256
    Admin->>Sub: GET GetTaskResult
    Sub->>OS: get object
    Sub->>Sub: verify sha256
    Sub-->>Admin: result JSON (attachment)
```

### API errors

Every 4xx from these endpoints has the same JSON body. Its keys are the same as the error keys in a failed task's `logs[0].metadata`:

```json
{ "error_code": "invalid_task", "error_message": "TaskId must be a GUID" }
```

| Status | `error_code` | When |
|---|---|---|
| 400 | `invalid_task` or `invalid_dataset_name` | The request or the task is not valid. This includes a form with an unknown `State` value or a bad `ErrorCode`, and a request over the size limit of `ReportResult` (73728 bytes). |
| 400 | `invalid_result` | `ReportResult`: the result file is not valid, or does not match the task. |
| 400 | `result_too_large` | `ReportResult`: the result file is over 65536 bytes. |
| 404 | `task_not_found` | No such task. `ReportProgress` and `ReportResult` also return this for a task that belongs to another TRE. |
| 404 | `study_not_found` | `GetStudy`, `GetStudyVersion` or `Cancel`: no such study, version or version ID. |
| 404 | `result_not_found` | `GetTaskResult`: the task is not `released`. |
| 409 | `task_already_reported` | `ReportResult` or `ReportProgress` for a task that is already final. When the task is `released`, the body also has `result_sha256` and `result_key`. |
| 409 | `invalid_transition` | `ReportProgress` asks to go back to an earlier phase. |
| 409 | `study_version_open` | `Enqueue` for a study whose latest version is still open. The body also has `version_id`, the open version. Cancel it or wait for it to end. |
| 409 | `task_busy` | `ReportProgress`, `ReportResult` or `Cancel` lost a race with other reports for the same task or version three times in a row; or `Enqueue` lost the version number to other submissions of the same study three times. Nothing was recorded. Send it again. |
| 409 | `task_canceling` | `ReportResult` or `ReportProgress` for a task the orchestrator is cancelling. Only the cancel confirmation (`ReportProgress` with `canceled`) is accepted. See "Cancelling a study version". |
| 409 | `task_already_finished` | `Cancel` for a version that already has an outcome other than `canceled`. |
| 500 | `internal_error` | `GetTaskResult`: the stored file does not match its SHA-256. The file is not returned. |
| 503 | `storage_unavailable` | The orchestrator bucket cannot be reached, or `ReportResult` lost a race and could not read the task again. For `ReportResult` the task keeps its phase and the Agent sends the report again at the next poll. |

`result_not_found` and `storage_unavailable` are returned by the API only. An Agent never reports them as a task result.

401 and 403 come from authentication and have no body. `ReportProgress`, `ReportResult` and `GetPendingTasksForTre` return 403 to any caller that is not a TRE Agent service account.

A `ReportResult` with state `EXECUTOR_ERROR`, `SYSTEM_ERROR` or `CANCELED` must have an `ErrorCode`, or it gets a 400. Any code that matches `^[a-z_]{1,64}$` is accepted, not only the ones listed below. `ErrorMessage` is at most 512 characters and has no control characters.

### Reporting progress

`POST /api/FederatedTask/ReportProgress` takes a JSON body:

```json
{ "task_id": "0b9a3c4e-6f1d-4c1e-9a52-3d7b8e2f1a10", "phase": "running" }
```

`phase` is `running`, `waiting_for_output_approval` or `canceled`. Any other value, or a `task_id` that is not a GUID, gets 400 `invalid_task`. A 200 response is `{ "phase": "<the phase the task is at now>" }`.

- The task is at an earlier phase: it moves to the new phase and the time is recorded.
- The task is already at that phase: 200, nothing changes. The Agent can send the same report again.
- The task is at a later phase: 409 `invalid_transition`.
- The task is final: 409 `task_already_reported`.
- The task is `canceling`: only `canceled` is accepted; `running` and `waiting_for_output_approval` get 409 `task_canceling`.
- `canceled` for a task that was not cancelled: 409 `invalid_transition`.
- Other reports for the same task won the race three times in a row: 409 `task_busy`. Nothing changed. Send the report again.
- The task belongs to another TRE, or does not exist: 404 `task_not_found`.

The Agent sends `running` when it picks a `queued` task up, `waiting_for_output_approval` after it has registered the result with the Egress app, and `canceled` once it has cleaned up a cancelled task.

### Reading a study

`GET /api/FederatedTask/GetStudy?studyId=<studyId>[&version=<n>]` returns the study's latest version, or version `n`. `GET /api/FederatedTask/GetStudyVersion?id=<versionId>` returns a version by the ID `Enqueue` gave. Both need `dare-control-admin`, and answer 404 `study_not_found` for an unknown study, version or ID. This is plain JSON, not TES:

```json
{
  "study_id": "study-001",
  "version": 2,
  "version_id": "5f0c9a52-2b8e-4c41-9d0e-7a3b1c6e8f20",
  "submitted_at": "2026-11-24T18:00:00Z",
  "code_sha256": "<lowercase hex SHA-256 of the code>",
  "state": "open",
  "outcome": null,
  "outcome_reason": null,
  "outcome_message": null,
  "outcome_at": null,
  "deadline_at": "2026-12-01T18:00:00Z",
  "ready_at": null,
  "aggregation_started_at": null,
  "aggregation_error_code": null,
  "pooled_result_sha256": null,
  "canceled_at": null,
  "versions": [1, 2],
  "hospitals": [
    { "tre": "hosp-a", "task_id": "0b9a3c4e-6f1d-4c1e-9a52-3d7b8e2f1a10", "phase": "released", "state": "COMPLETE",
      "times": { "queued_at": "…", "running_at": "…", "waiting_for_output_approval_at": "…", "released_at": "…" },
      "result_sha256": "<sha256 of the released file>" },
    { "tre": "hosp-b", "task_id": "7c2d1e0f-3a4b-4c5d-8e9f-0a1b2c3d4e5f", "phase": "queued", "state": "QUEUED",
      "times": { "queued_at": "…" } }
  ]
}
```

- `state` is `open`, `canceling` or `ended`, and `outcome`, `outcome_reason`, `outcome_message` and `outcome_at` are set once the version is decided (see "Studies and versions"). `deadline_at` is fixed at intake. `canceled_at` is `outcome_at` when an admin cancelled the version. `ready_at`, `aggregation_started_at`, `aggregation_error_code` and `pooled_result_sha256` follow the pooling (see "Pooling the results").
- None of this is a TES state. Each hospital's `state` is. When the TES API for studies comes (ANZE-6134), a version maps to the standard states without new ones: `open` → `RUNNING`, `canceling` → `CANCELING` (whatever the outcome, until every hospital has confirmed its cleanup; a hospital that never comes back leaves the version there, which blocks nothing), then by the outcome: `ready_for_aggregation` → `RUNNING`, `aggregated` → `COMPLETE`, `failed` → `EXECUTOR_ERROR` for a failure of the researcher's code or `SYSTEM_ERROR` (`invalid_task`, `image_not_approved`, `run_failed`, `internal_error`, `aggregation_timeout`), and `incomplete` or `canceled` → `CANCELED`, each with the reason or error code in metadata, as a rejected part is `CANCELED` with its `error_code` today.
- `versions` lists every version number of the study; only `GetStudy` sends it.
- `hospitals` is in `fed.sites` order. `phase`, `state` and `times` are each hospital's task phase, TES state and `<phase>_at` times, as in "Task phases and state". `error_code` is there for a rejected or failed part, `result_sha256` for a released one, and `scan_rules_version` and `scan_rules_sha256` for a part whose code was scanned (see "Code scan"). Result content never is; read it with `GetTaskResult` and the part's `task_id`.

### Cancelling a study version

`POST /api/FederatedTask/Cancel?id=<versionId>` cancels a whole study version. It needs the `dare-control-admin` role. It gives the version the `canceled` outcome and cancels every unfinished part, in one save. A 200 response is the version, as `GetStudyVersion` returns it. There is no cancel of one hospital's part from the API.

- A `queued` part, which no hospital has picked up, moves straight to `canceled`. If the Agent picked it up at the same moment, its `running` report is refused, and it does not run the task.
- A `running` or `waiting_for_output_approval` part moves to `canceling`. It stays in `GetPendingTasksForTre` (listed first, even if the TRE was renamed since) until the hospital confirms. A TRE rename does not fail it. There is no give-up: the confirmation means the hospital closed the review and removed the result, so a hospital whose Agent never comes back leaves its part, and the version, `canceling`. That blocks nothing, and the part can never release.
- Final parts keep their phase. A version with every part final after the cancel is `ended`; one with a part still `canceling` is `canceling`. Either way it no longer blocks a new version of the study.
- A version already cancelled: 200, nothing changes. A version with another outcome (`ready_for_aggregation` or `incomplete`): 409 `task_already_finished`. A version whose parts are all final but which the sweep has not decided yet is cancelled. An unknown version ID: 404 `study_not_found`.

While a task is `canceling`, Submission refuses every `ReportResult` (an approved result, a rejection or an error) with 409 `task_canceling`, before any file is written. So a reviewer's decision made at the same moment as a cancel never releases the result: the task can only end `canceled`.

On a `canceling` task, the Agent:

1. stops and removes the run of the task's code, if there is one (a Docker error stops here, and the next poll tries again). This comes first so the old code stops even when the Egress app is unreachable, since a new version of the study may already be queued;
2. withdraws the review if it is still pending, so no decision is accepted any more, and closes the request in the Egress app (an Egress error stops here, and the next poll tries again);
3. deletes the saved result from `federated-outputs` (a store error stops here, and the next poll tries again);
4. reports `ReportProgress` with `canceled`. Before Submission accepts it, it deletes any result file stored for the task (one can exist if a cancel raced a result report). A storage error answers 503 `storage_unavailable`, and the Agent confirms again at the next poll.

Each part is safe to repeat, so if one fails the next poll does them again. The review record and its withdrawn marker stay in the TRE as the audit of what was reviewed; they hold no data. There is no standard TES cancel yet. `POST /tasks/{id}:cancel`, for DAAMS, is planned with the TES API task and will apply the same rules.

### Pooling the results

Each hospital returns a partial result; the researcher's `aggregate(results)`, in the same file as `site_analysis`, combines them. It runs in the **aggregator**: the Agent image with `FederatedTaskSettings:Role = Aggregator` (see "Aggregator"), not Submission. Starting containers needs the Docker socket, which is root on its host, and Submission, which every hospital and admin talks to and which holds every released result, gets none.

1. The sweep marks a version `ready_for_aggregation` (see "Studies and versions") and records `ready_at`.
2. The aggregator polls `GET /api/FederatedAggregation/GetPendingAggregations` (role `dare-federated-aggregator`), oldest first. Each job has the version's ID, study, number, the task as submitted, `code_sha256`, and `resultsJson`: a JSON array of each released hospital's `result.value`, in `fed.sites` order. Submission reads each stored file back and checks its SHA-256 first. A file that is missing or fails its check fails the version with `internal_error`; if storage is unreachable, the version waits for the next poll. The array holds no hospital names, task IDs or anything else: `aggregate()` sees the values only.
3. The aggregator recomputes the code's SHA-256 and compares it with `code_sha256` and the task's `fed.code_sha256` (a mismatch fails with `invalid_task`, and nothing runs), and checks the task's image against its own `ApprovedImages` (`image_not_approved`). It reports `running`, stages only the code and `results.json`, and runs `run-analysis --aggregate /code/analysis.py /data/results.json /outputs/result.json` (see `runner/README.md`) under the same container rules and limits as at a hospital (see "Running a code task"): no network, read-only root, no logs kept, `MaxRunMinutes`.
4. On exit it reads `result.json` and reports with `POST /api/FederatedAggregation/ReportAggregation` (`multipart/form-data`: `VersionId`, `Phase`, `ErrorCode`, and the pooled document as file part `result`):
   - `aggregated`: Submission checks the pooled document (below) against the version, stores it under `studies/<versionId>/<sha256>.json` in `federated-results`, apart from the site results, and the outcome becomes `aggregated` with `pooled_result_sha256`.
   - `failed`: one of `invalid_task`, `image_not_approved`, `code_error`, `invalid_result`, `result_too_large`, `timeout`, `out_of_memory`, `run_failed`, `internal_error`. Submission stores the code and a fixed message; it takes no text from the aggregator. A `results.json` the runner can't read is reported as `internal_error`, since the aggregator wrote it.
   - A report for a version that is no longer `ready_for_aggregation` gets 409 `task_already_finished`, except the same final report sent again (200).
5. Nothing `aggregate()` writes (output, traceback, exception text) reaches Submission or its logs. The runner's own message goes to the aggregator's log only, as at a hospital.

Pooled document:

```json
{
  "schemaVersion": 1, "studyId": "study-001", "version": 2, "versionId": "5f0c9a52-…", "codeSha256": "<hex>",
  "sites": 2, "completedAt": "2026-11-25T15:00:00+00:00",
  "result": { "kind": "aggregate", "n": 240, "value": { "n": 240, "mean_age": 41.0 } }
}
```

`sites` is how many hospital results went in, never which. `result.n` must equal `result.value.n`. The same limits as a site result document apply (65,536 bytes, nesting at most 16 levels).

Results are values, never files: for a graph, `aggregate()` returns the data behind it (bins and counts, points), and the researcher draws it in the SPE. Learning over several rounds is not supported (`fed.round` is 1).

Stuck or broken pooling. A version that waits longer than `AggregationTimeoutMinutes` (default 24 hours) from `ready_at` fails with `aggregation_timeout`, so an aggregator that is down or misconfigured can't leave it waiting for ever. `POST /api/FederatedAggregation/RetryAggregation?id=<versionId>` (`dare-control-admin`) puts a version that `failed` with `aggregation_timeout`, `run_failed` or `internal_error` back to `ready_for_aggregation`, with a new `ready_at`: the stored code and released results are the same, so a retry can only produce what the first run would have. Any other failure is the researcher's code, which would fail again, and gets 409.

**Known limitation:** `aggregate()` is researcher code, so it could pass one hospital's result through unchanged. In the PoC, only DAAMS export approval checks the pooled result.

### Reporting a result

`POST /api/FederatedTask/ReportResult` is `multipart/form-data`.

| Field | Rule |
|---|---|
| `TaskId` | The task GUID. |
| `State` | `COMPLETE`, `CANCELED`, `EXECUTOR_ERROR` or `SYSTEM_ERROR`. |
| `RejectedAt` | `CANCELED` only, and required there: `input_checks` or `output_approval`. Any other value is 400 `invalid_task`. It is not allowed with another state. |
| `ErrorCode`, `ErrorMessage` | Error states and `CANCELED`. See the limits above. A rejection (`CANCELED`) also needs a non-empty `ErrorMessage`. |
| `DatasetSha256` | Error states and `CANCELED`. 64 lowercase hex characters. On `COMPLETE` Submission takes it from the result file. |
| `ScanRulesVersion` | Optional, any state. The version of the code-scan rules the task was checked with (see "Code scan"). At most 64 characters, no control characters. |
| `ScanRulesSha256` | Optional, any state. The SHA-256 of that rule file. 64 lowercase hex characters. |
| `result` (file) | Required for `COMPLETE`. Not allowed for `CANCELED` or an error state. One file only. |

On `COMPLETE` the result file is the only content Submission accepts, with one exception: `ScanRulesVersion` and `ScanRulesSha256`. They are form fields, not part of the result document, so recording the rules does not change the bytes the reviewer approved. The Agent sends both on every final report after the scan ran (released, failed or rejected, including the scan's own rejection), and neither for a task refused before the scan (`invalid_task`, `image_not_approved`).

A report is accepted from any phase that is not final, so a `queued`, `running` or `waiting_for_output_approval` task can be finished. `COMPLETE` moves the task to `released`. `CANCELED` moves it to `rejected_by_input_checks` or `rejected_at_output_approval`, as `RejectedAt` says. `EXECUTOR_ERROR` and `SYSTEM_ERROR` move it to `failed`.

Submission checks the caller first. Only the Agent service account of a TRE is accepted: the token `azp` is the TRE's Keycloak client id, and `preferred_username` is `service-account-<clientId>`, with role `dare-tre-admin`. Anyone else gets 403, whatever the request holds. The TRE comes from the token, never from the form or the file. A request over 73728 bytes is rejected by the framework with 400 `invalid_task`, before this check.

The `result` file:

- It is at most 65536 bytes, or the answer is 400 `result_too_large`.
- It is UTF-8 JSON, nested at most 16 levels, with no duplicate property names. It must match the result document and the task (`taskId`, `tre`, `studyId`, `operation`, `dataset`), or the answer is 400 `invalid_result`. The message names the field and never repeats the content.
- The file name of the upload is ignored. Submission builds the storage key itself.

A 200 response has `result`, `result_bucket`, `result_key` and `result_sha256`. The last three are only for `COMPLETE`.

If Submission cannot write the file, it answers 503 `storage_unavailable` and the task keeps its phase. If the Agent gets 400 `invalid_result` or `result_too_large`, it sends a follow-up `EXECUTOR_ERROR` report with the same code. A 409 for a `released` task carries `result_sha256` and `result_key`, because the Agent cannot call `GetTask`. The Agent compares the sha256 with its own copy and, if they are equal, tags the TRE copy with the key. The Agent only gets this 409 when two reports for the same task race. It does not ask again later.

The tag can be missing on a released file. This happens when the Agent does not get the 200 (a timeout after Submission saved the result, or an Agent restart), or when tagging fails. Submission then has the task as `released` and never gives it to the Agent again, so nothing adds the tag later. A missing tag does not mean the file was not released. To check, compare the TRE copy's SHA-256 with `result_sha256` from `GetTask`.

TODO: re-tag released TRE copies that have no `released-key` (follow-up ticket).

## Output approval

Every computed result is reviewed at the hospital before it leaves the TRE. The review happens in the Data Egress app (`Egress/`, forked from Swansea's DARE-Control). Native 5S-TES outputs are reviewed in the same app.

Registering. After the Agent saves the result, it writes the review record (below) as pending, then calls the Egress API's `POST /api/DataEgress/AddNewDataEgress` with the task ID as `SubmissionId`, the kind `federated`, the study ID, the image and command, the result bytes and their SHA-256. The Egress app keeps its own copy of the bytes, so the reviewer reads exactly what was registered, and the Egress app needs no access to `federated-outputs`. It refuses the registration (400) if the SHA-256 is not the hash of that UTF-8 text. A second registration of the same task is accepted as already done. Then the Agent writes the registration marker (below) and reports `waiting_for_output_approval`. If one of these fails, the task keeps its phase and the next poll goes on from where it stopped. If the registration keeps failing (the Egress app is down, unreachable or refuses it) for `EgressUnreachableGiveUpMinutes` (default 1440, one day) since the result was first sent for review, or since the Agent started if that is later, the task fails with `SYSTEM_ERROR` and `internal_error`. Its review is withdrawn first (see "Withdrawing"), because the Egress app may have saved the request even though the call failed. Each poll fetches one token for all its registrations.

The Agent calls the Egress API with a client-credentials token of its own Keycloak client in the Egress realm (`FederatedEgressSettings:BaseUrl`, `ClientId` and `ClientSecret`; the service account has the role `dare-tre-admin`), at `ApiEndpoints:EgressApiUrl`. This section is separate from `DataEgressKeyCloakSettings`, which native egress uses, and has no defaults. If any of the three is not set, the Agent does not register the result. It logs an error once per poll and the task keeps its phase.

Deciding. The reviewer sees the study ID, the code and the result with its SHA-256. The study details and the data permit are not provided yet; they come with the 5S-Crate (task 4.1). A rejection needs a reason. The Egress app saves the decision first, then calls the Agent's `POST /api/Submission/EgressResults` with the reviewer, the decision time, the reason and the SHA-256 of the bytes the reviewer saw. If that call fails, the Egress API sends the stored decision again once a minute until the Agent takes it, and the request stays in the "Unprocessed" list until then. The reviewer who made it can also send it again with Complete & Close. A saved decision cannot be changed: if the form differs from it, the Egress app refuses and names the stored decision. A 400, 404 or 409 from the Agent's own `EgressResults` handler is final: its body carries `"refusal": "final_refusal_by_tre_agent"`, and the Egress app then closes the request with "The TRE refused this decision", shows that on its page and stops sending it. The same status without that marker (from a proxy, an ingress or a wrong `TreAPISettings:Address`), network errors and other answers are retried. Only one decision per request is saved: the request rows carry Postgres's `xmin` row version, so if two reviewers complete the same request at once, the later save fails and that reviewer gets "This request was just decided by someone else". A stored decision that has not reached the Agent yet can be sent again only by the reviewer who made it.

For a federated task, `EgressResults` accepts the decision only from the Egress app's own login (role `data-egress-admin`). Any other caller with a GUID `SubId`, `dare-tre-admin` included, gets 403 before the task is looked up, so it cannot tell whether a task has a review. Otherwise it returns 404 for a task with no review record, 400 for a decision with no reviewer, time or SHA-256, or a rejection without a reason, and 409 for a SHA-256 that differs from the registered one, a decision that differs from one already recorded, or a task that was withdrawn. The same decision sent again is accepted with no change. After recording the decision, it queues a Hangfire job (`ActOnDecision`) that releases or rejects the task at once, so a decision does not wait for the task to come up in the poll, which hands out at most 50 tasks. The job is retried if it fails, and the poll acts on a recorded decision too. A `SubId` that is not a GUID runs the native code as before. A GUID with no review record gets 404 and never runs the native code.

The Egress app logs in to the TRE realm with a user that has `data-egress-admin` (stored with `TreCredentials/UpdateCredentials`), with a client whose tokens carry an audience the Agent accepts (`TreKeyCloakSettings:ValidAudiences`).

### Review record

The Agent keeps the review in the TRE bucket next to the result, as `federated-outputs/<taskId>.review.json`. It survives a restart and is readable by custodians with `federated-outputs-read`. It is shaped as a 5S-Crate 0.4 `DisclosureCheck`:

```json
{
  "@type": "AssessAction",
  "additionalType": "https://w3id.org/shp#DisclosureCheck",
  "actionStatus": "http://schema.org/CompletedActionStatus",
  "object": { "sha256": "ae28cc6d7117376e2245e59e139d2a58cea8b65d5bf69de921210decfa84054b" },
  "startTime": "2026-11-11T22:54:09.8825703+00:00",
  "endTime": "2026-11-11T22:54:35.9042277+00:00",
  "agent": "reviewer",
  "description": null
}
```

- `actionStatus` is `PotentialActionStatus` while waiting, `CompletedActionStatus` on approval and `FailedActionStatus` on rejection.
- `object.sha256` is the SHA-256 of the registered result. Only a file with this hash is released.
- `startTime` is the registration time, `endTime` the decision time, and `agent` the reviewer.
- `description` holds the reason. schema.org defines it; the 5S-Crate profile does not, so it is our extension.

After the Egress app accepts the registration, the Agent writes an empty object `federated-outputs/<taskId>.registered` next to the record. A pending record without this marker is registered again at the next poll. A decided record needs no marker, because the decision came from the Egress app.

The bucket has no conditional write, so the poll and the Egress decision never write the same object. The poll (which never runs twice at once) writes the review record only once, as pending, before it calls the Egress app. After that, only the Egress decision writes the record, and the poll writes only the marker. A decision that arrives while the poll registers the task is never overwritten. A record from an older version can have a `registered` field; the Agent ignores it.

Once a result has a review record, it is never computed again. If the saved result is gone, a rejection is still reported as a rejection, and anything else as `result_changed_after_review`.

Withdrawing. If the saved result changes or disappears while its review is still pending, or its registration gives up, the Agent first writes an empty object `federated-outputs/<taskId>.withdrawn`, then asks the Egress app to close the request (`POST /api/DataEgress/WithdrawEgress` with the task ID and a reason), then reports the failure: `result_changed_after_review` when the result changed, `internal_error` when registration gave up. If that report does not get through, the next poll reports it again; a withdrawn review is never registered again. A closed request leaves the reviewers' queue and cannot be decided; its page shows the reason. If the Egress app cannot be reached, the task still fails: the marker makes `EgressResults` refuse any later decision with 409, and the review record stays pending.

## The federated TES profile

Every federated task is a code task. Submission checks it against these rules at intake, and the Agent checks it again before its input checks.

### Code tasks

A code task runs the researcher's Python file with `run-analysis` in an approved runner image (see `runner/README.md`).

| TES field | Rule |
|---|---|
| `executors` | Exactly 1. `image` must be in Submission's `FederatedTaskSettings:ApprovedRunnerImages`. `env`, `workdir`, `stdin`, `stdout` and `stderr` must not be set (empty values are allowed). |
| `executors[0].command` | Exactly `["run-analysis", "/code/analysis.py", "/data/<dataset>", "/outputs/result.json"]`. No other arguments, so `--debug` is refused: its traceback can contain data values. |
| `inputs` | Exactly 2, in either order. The **code input**: `path` `/code/analysis.py`, `type` `FILE`, the code in `content`, no `url`, at most 262,144 bytes (256 KiB) in UTF-8. The **dataset input**: `url` `dataset://<name>`, `path` `/data/<name>`, `type` `DIRECTORY`, no `content` (in TES, `content` would replace the hospital's data). |
| `outputs` | Exactly 1: `path` `/outputs/result.json`, `type` `FILE`, `url` `result://`. TES requires a `url`; any real address is refused, so no runtime can upload the result past output approval. The result goes through output approval at the hospital and back to Submission, as for every task. |
| `volumes` | Must be null or empty. |
| `resources` | May be left out. If given, `cpu_cores`, `ram_gb` and `disk_gb` must each be more than 0 and at most `MaxCpuCores`, `MaxRamGb` and `MaxDiskGb`. `zones`, `preemptible` and `backend_parameters` must not be set. |
| `tags` | `fed.sites` lists the hospitals (TREs) the study version goes to, separated by `\|`, for example `hosp-a\|hosp-b`. Each entry is trimmed and lowercased. Fewer than 2 TREs, an empty entry, a TRE named twice or more than 20 TREs is refused (`invalid_task`), and so is the whole submission if any entry is not a registered TRE (see "Studies and versions"). `fed.study_id` must not be empty. Both are at most 1024 characters, the limit for strings in the result document. `fed.code_sha256` is required: the lowercase hex SHA-256 of the code's UTF-8 bytes. It must match the code, so the hash the hospital reviews is the hash of the code that runs. `fed.round` is optional and must be `1`: a study version runs one round of analysis, and later rounds (federated learning) come after the PoC. Without it the round is 1. Tags without the `fed.` prefix are kept and ignored. |
| `name`, `description` | Accepted and ignored. |

The dataset name must match `^[a-z0-9][a-z0-9_-]{0,62}$`.

Every rule that fails gives `invalid_task` (or `invalid_dataset_name` for the name), and the message names the rule. Nothing is stored.

The stored task has the operation `run-analysis`. The whole task, with the code, is kept.

Example:

```json
{
  "executors": [
    {
      "image": "python-runner:1",
      "command": ["run-analysis", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"]
    }
  ],
  "inputs": [
    { "path": "/code/analysis.py", "type": "FILE", "content": "def site_analysis(df):\n    return {'n': len(df), 'mean_age': float(df['age'].mean())}\n" },
    { "url": "dataset://cohort_2024", "path": "/data/cohort_2024", "type": "DIRECTORY" }
  ],
  "outputs": [{ "path": "/outputs/result.json", "url": "result://", "type": "FILE" }],
  "resources": { "cpu_cores": 2, "ram_gb": 8 },
  "tags": {
    "fed.sites": "my-tre|other-tre",
    "fed.study_id": "study-001",
    "fed.code_sha256": "<lowercase hex SHA-256 of the content>"
  }
}
```

The code reaches only the hospitals in `fed.sites`, as it was submitted. The code travels inside the task. Submission checks its hash at intake, and stores the task once for the version and once for each hospital's part. The stored task is init-only in code and EF refuses to save a change to it; bulk updates and raw SQL are not covered by that guard; Submission runs none on this column (the only raw SQL on these rows is in an old migration and does not touch the task). Each Agent gets only its own TRE's tasks, through `GetPendingTasksForTre` with its service account. As part of its profile check, before any input check or data read, the Agent recomputes the hash. If the code no longer matches `fed.code_sha256`, the task is rejected (`rejected_by_input_checks`, `invalid_task`, "tag 'fed.code_sha256' does not match the code"), and no data is read.

What the hash check catches: code changed on the way or in storage without its tag. What it does not catch: someone who can write to Submission's database could change both the code and its tag, because they are stored together. The orchestrator's database is trusted for the PoC. Closing that gap needs a signature made at intake that the Agent can verify, which is not built yet.

### Running a code task

The Agent runs a code task in a Docker container, through the Docker socket (`FederatedTaskSettings:DockerEndpoint`). The poll moves a run one step and never waits for it:

1. The input checks pass (the image is approved, the code passes the code scan, one `.csv` and a `schema.json` are in the dataset folder). If the TRE already holds `MaxConcurrentRuns` code runs, the task is not started and keeps its phase. Every run that is still present counts, including one that has exited and is not removed yet, not only those that are running. A `queued` task stays `queued`.
2. A `queued` task is reported `running`. The Agent hashes the CSV and stages the CSV, `schema.json` and the code into Docker volumes, then starts the container. The poll returns, and the task stays `running`.
3. At each later poll the Agent finds the container. While it runs, nothing happens. If it has run for `MaxRunMinutes`, the Agent kills it and reports `timeout`.
4. When the container has exited, the Agent reads `/outputs/result.json`, checks it and maps it (see "Result of a code run"). It saves the result file, removes the run, and the task goes to output approval as any other.
5. If the task fails, the Agent reports the failure and removes the run once the report is accepted. If the report was lost, the next poll finds the run and reports the same outcome again. The code does not run twice.

A `running` code task that has no run and no saved result (the run was lost, or the start failed part-way) is started again, so its code runs again. The Agent logs this as a warning.

The Agent also removes old runs at every poll (the sweep). A run is removed when its task has a saved result, or when the task is not pending and the run is older than twice `MaxRunMinutes`. The sweep also removes staged data that has no run container (volumes, which can hold a copy of the dataset, or a staging container), as a removal or a failed start that was cut short leaves it. If one removal fails, the sweep goes on with the others and the next poll tries again. Use at most one Agent of each role (hospital, aggregator) per Docker daemon. The sweep treats its own staged data without a run container as left over, which is safe only because the same Agent's poll stages and starts runs, one poll at a time. Runs are labelled with their owner (`fed.owner`), so a hospital Agent and the aggregator on one daemon leave each other's alone; a second Agent of the same role could remove the other's staged data while it starts a run.

The container:

- The command is built by the Agent, `run-analysis /code/analysis.py /data/<dataset> /outputs/result.json`. The task's own command, `env` and other executor fields are never used.
- No network. Root file system read-only, user `1001:0`, all capabilities dropped, `no-new-privileges`, at most 256 processes.
- `/data` and `/code` are read-only volumes. `/outputs` is the only writable volume that is kept. `/tmp` is a `noexec` tmpfs.
- Swap is off (the swap limit equals the memory limit).
- stdout and stderr are never kept: the log driver is `none`. Nothing the code prints can be read.
- The Agent reads only `/outputs/result.json`, at most 65,536 bytes. More is `result_too_large`. Nothing else the code writes is read.

Limits:

| Limit | From the task | Default | Maximum |
|---|---|---|---|
| CPU | `resources.cpu_cores` | `DefaultCpuCores` | `MaxCpuCores` |
| Memory | `resources.ram_gb` | `DefaultRamGb` | `MaxRamGb` |
| `/tmp` (disk) | `resources.disk_gb` | `DefaultDiskGb` | `MaxDiskGb` |

A task with no value, or a value that is 0 or less or not a finite number, gets the default. The default is itself capped at the maximum. A value above the maximum is reduced to the maximum, and the Agent logs it. (Submission refuses such a request at intake. The Agent clamps it too, because its own maximum can be lower than Submission's.) A very small value is raised to a floor, and logged: memory at least 256 MiB, `/tmp` at least 16 MiB, CPU at least 0.01 cores. Docker reads 0 as no limit, so a floor keeps every run limited.

Every file the code writes, in `/outputs` or in `/tmp`, is capped at the `/tmp` allowance by an `fsize` ulimit. **Known limitation:** many files that are each under that size can still fill the host's disk through `/outputs`, which is a volume with no size limit. Production needs a size-limited volume, or the limits of the operator's pod.

**Security note:** the Agent needs the Docker socket, which is root-equivalent on the host. This is acceptable for the PoC. Production needs a rootless runtime, a dedicated runner, or the hospital operator that starts the pod (see `runner/README.md`).

### Result of a code run

The Agent maps `result.json` (format in `runner/README.md`) as follows. It never forwards text the code produced.

- `status` `ok`: the Agent checks that `n` is a non-negative integer equal to `result.n`, that `datasetSha256` is the hash it computed before the run, and that the whole result document is valid as Submission checks it. The result is then the `analysis` result document (see "Result document").
- `status` `error` with a code the runner uses: the Agent reports that code with a fixed message (see "Error codes"). The runner's own message can hold the researcher's text, so it goes to the Agent log only.
- Anything else (not JSON, no `status`, an unknown code, a bad `n`, a wrong hash, a document that is not valid): `invalid_result` with the fixed message "the run produced a result file the TRE cannot accept".
- No `result.json`: `out_of_memory` if Docker says the container was killed for memory, else `run_failed`.
- More than 65,536 bytes: `result_too_large`, with the same message as `invalid_result`.

**Known limitation:** at the hospital, a researcher sees only which kind of failure happened, not the column, line or exception. To find the cause, reproduce it in the SPE with synthetic data (`run-analysis --debug` there), or ask the TRE admin, who sees the runner's full message in the Agent log. The reason: an error report does not go through output approval, and the code can write `result.json` itself, so the Agent cannot pass on any text from it.

**Known limitation:** a little still reaches the researcher without output approval, chosen by the code: which failure code it ends with, whether it succeeds at all, and how long it runs (visible in the phase times). No text the code writes is forwarded.

**Known limitation:** the Agent holds the whole CSV in memory while it stages it, so a very large upload can exhaust the Agent's memory. Uploaders must keep a dataset within the runner's 256 MiB limit.

### Task phases and state

A task has a phase. It says where the task is, from intake to release. `GetTask` returns a valid TES task, so `state` is always a standard TES state. The phase is in the metadata.

| Phase | TES `state` | When |
|---|---|---|
| `queued` | `QUEUED` | The task is enqueued. |
| `running` | `RUNNING` | The Agent has picked the task up. |
| `waiting_for_output_approval` | `RUNNING` | The result is saved in the TRE and waits for a reviewer's decision in the Egress app. Set by the Agent. |
| `released` | `COMPLETE` | The result file is stored in the orchestrator. |
| `rejected_by_input_checks` | `CANCELED` | The task was refused before it ran. The error code is one of `invalid_task`, `image_not_approved`, `unsafe_code`, `invalid_dataset_name`, `dataset_not_found`, `ambiguous_dataset` or `schema_not_found`. |
| `rejected_at_output_approval` | `CANCELED` | A reviewer refused the result. The error code is `rejected_by_reviewer` and the message is the reviewer's reason. |
| `canceling` | `CANCELING` | The orchestrator cancelled a task a hospital had picked up. The hospital is closing the review and removing the result. |
| `canceled` | `CANCELED` | The task was cancelled: before any hospital picked it up, or after the hospital confirmed its clean-up. |
| `failed` | `EXECUTOR_ERROR` or `SYSTEM_ERROR` | Any other error. The Agent sends the state. `SYSTEM_ERROR` is for `internal_error`, `tre_renamed` and `result_changed_after_review`. `EXECUTOR_ERROR` is for the other codes. |

A task only moves forward: `queued`, `running`, `waiting_for_output_approval`, then one final phase (`released`, a `rejected_*` phase, `failed` or `canceled`). A cancel can also move a `running` or `waiting_for_output_approval` task to `canceling`, which ends in `canceled`. A final phase never changes. The Agent decides between a rejection and a failure by the error code.

The result is in `logs[0].metadata`. Every key and value is a string.

- `phase`: the current phase, as in the table.
- `<phase>_at` for each phase the task has reached, for example `queued_at` and `running_at`. The value is a UTC time in ISO 8601 form (`O` format). A task rejected before it ran has no `running_at`. `logs[0].start_time` is `running_at`. `logs[0].end_time` is the time of the final phase.
- On `released`: `result_bucket`, `result_key` and `result_sha256` (where the result file is stored in the orchestrator, and its SHA-256). On a rejection or a failure: `error_code` and `error_message`.

- The values the code returned are in the result file, not in the metadata.
- `dataset_sha256` is added whenever the Agent read the dataset file, on success and on errors raised after the read (including `invalid_dataset`). It is the lowercase hex SHA-256 of the exact bytes of the CSV. It is absent when the file was not read: every input-check rejection (`invalid_task`, `image_not_approved`, `unsafe_code`, `invalid_dataset_name`, `dataset_not_found`, `ambiguous_dataset`, `schema_not_found`), `tre_renamed`, and `internal_error` before the read. An empty file gives `invalid_dataset` with the hash.
- `round` is always there: the round of the study version the task belongs to (`fed.round`), `1` in the PoC.
- `scan_rules_version` and `scan_rules_sha256` are added whenever the code scan ran: the version and SHA-256 of the hospital's rule file at the time of the scan (see "Code scan"). They are on released, failed and rejected tasks alike, including an `unsafe_code` rejection, and absent for a task refused before the scan (`invalid_task`, `image_not_approved`). A task that waited for output approval reports the rules it was checked with, even if the hospital changed its rule file in the meantime.

### How the phases fit the standards

- **GA4GH TES.** `GetTask` always returns a valid TES task with a standard TES `state`. The phase, `error_code` and `error_message` are in `logs[0].metadata`, which TES leaves free-form. TES defines no error codes, so ours clash with nothing. `RUNNING` for `waiting_for_output_approval` means the task is still in progress, which is what a TES client needs to know. TES `PAUSED` would mean someone has to resume the task, and TES has no resume call. The phase tells `running` and `waiting_for_output_approval` apart, so the Agent and DAAMS read the phase, not the state.
- **`CANCELED` for a rejection is a stretch.** TES says `CANCELED` means the user canceled the task. A hospital that refuses a task is not that, but TES has no "rejected" state. `SYSTEM_ERROR` and `EXECUTOR_ERROR` say that something broke, and a refusal is a decision, not a fault. `CANCELED` is the closest: the task stopped on purpose, the state is final, and TES clients don't retry it. A generic TES client that reads only `state` can tell the researcher that they canceled the task. A client that reads the metadata sees the real phase and reason. DAAMS should read the status from the metadata (task 4.3).
- **Upstream 5S-TES** has its own status list. Only `CancelTask` sets `CANCELED`, so there is no rejection mapping to follow.
- **Five Safes RO-Crate (TRE-FX).** The profile records checks, such as validation and sign-off, as actions in the crate. When the crate becomes the audit trail, map the input-check and output-approval rejections to those actions instead of a separate naming. Confirm the profile's exact terms before that work.

### Result document

The result file is one JSON document. The property names are camelCase. `schemaVersion` is `1`. `tre` is the TRE name in lowercase.

```json
{
  "schemaVersion": 1,
  "taskId": "0b9a3c4e-6f1d-4c1e-9a52-3d7b8e2f1a10",
  "tre": "my-tre",
  "studyId": "study-001",
  "operation": "run-analysis",
  "dataset": "cohort_2024",
  "datasetSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "completedAt": "2026-11-05T09:30:00+00:00",
  "result": {
    "kind": "analysis",
    "n": 120,
    "value": { "n": 120, "mean_age": 41.2 }
  }
}
```

`operation` is `run-analysis` and `result.kind` is `analysis`. `result.n` is the non-negative integer the researcher's code returned as `n` (it must equal `result.value.n`), and `result.value` is the dict it returned. Any other kind is refused.

The whole file is at most 65,536 bytes, and JSON nesting at most 16 levels. The runner limits the researcher's dict to 61,440 bytes so that the document fits.

The file is stored under the key `<treId>/<taskId>/<sha256>.json` in the orchestrator bucket `federated-results`. `treId` is the TRE id in Submission and `sha256` is the SHA-256 of the file.

### Reading the result

`GET /api/FederatedTask/GetTaskResult?id=<taskId>` needs `dare-control-admin`. It reads the file from the orchestrator bucket, checks its SHA-256 against the one on the task, and returns it as the attachment `<taskId>.json` with `X-Content-Type-Options: nosniff`. The errors are in the table above: 404 `task_not_found` or `result_not_found`, 503 `storage_unavailable`, and 500 `internal_error` when the file fails the check.

TODO: researcher access scoped by study once study_id → project mapping exists

### Error codes

| Code | Meaning |
|---|---|
| `invalid_task` | The task does not follow the profile. The message says which rule failed. |
| `image_not_approved` | The executor image is not in this TRE's `ApprovedImages`. The message names the image. |
| `unsafe_code` | The hospital's code scan refused the code (phase `rejected_by_input_checks`, state `CANCELED`). The message lists each finding as `<rule-id> line <n>: <message>`, sorted by line and separated by `; `, for example `import-not-allowed line 1: 'socket' is not on this hospital's list of allowed imports`. It is cut to 512 characters and then ends with `; … and <k> more`. Code that does not parse gives `parse-error line <n>: the code could not be parsed`, and code that declares an encoding other than UTF-8 on line 1 or 2 gives `encoding-not-utf8 line <n>: ...` (Python would run different code from the code the scan reads). The message never repeats code beyond the name a rule quotes. |
| `invalid_dataset_name` | The name does not match `^[a-z0-9][a-z0-9_-]{0,62}$`. |
| `dataset_not_found` | No `.csv` file under `dataset/<name>/`. From a code run, the runner found no dataset file (the input checks normally catch this first); the message is "the runner found no dataset file". |
| `schema_not_found` | No `schema.json` in `dataset/<name>/`. An input check, so the task is rejected, and the message names the dataset. If the runner itself finds no schema, the message is "the runner found no schema.json for the dataset". |
| `ambiguous_dataset` | More than one `.csv` file under `dataset/<name>/`. From a code run, the runner found more than one dataset file (the input checks normally catch this first); the message is "the runner found more than one dataset file". |
| `invalid_dataset` | Runner code. The file is empty, is not UTF-8, is not valid CSV (for example a stray `"` inside a field), or a row has the wrong number of fields. The message is "the runner could not read the dataset as CSV". |
| `internal_error` | An unexpected failure, for example storage is unreachable, or the code scan could not run (message "the code scan could not run"). The message is generic, so that bucket names and hosts do not leave the TRE. The details are in the Agent log. |
| `tre_renamed` | The TRE was renamed in Submission before the task finished. Set by Submission, not the Agent. Enqueue the task again under the new name. |
| `rejected_by_reviewer` | A hospital reviewer rejected the result in the Egress app (phase `rejected_at_output_approval`, state `CANCELED`). The message is the reviewer's reason, at most 512 characters, with control characters removed. No file is released. |
| `result_changed_after_review` | The result was approved, but the saved file in `federated-outputs` no longer has the SHA-256 the reviewer saw, or is gone (state `SYSTEM_ERROR`). Nothing is released. |
| `timeout` | A code run was still going after `MaxRunMinutes`, and the Agent killed it. The message is "the code ran longer than the TRE's limit of N minutes". |
| `out_of_memory` | A code run ended without `result.json` and Docker says it was killed for memory. The message is "the code ran out of memory". |
| `run_failed` | A code run ended without `result.json` for any other reason. The message is "the run ended without a result". |
| `dataset_too_large` | Runner code, like `invalid_dataset` above. The message is "the dataset is larger than the runner's 256 MiB limit". |
| `invalid_schema` | Runner code. The message is "the dataset's schema.json is not valid". |
| `schema_mismatch` | Runner code. The message is "the dataset does not match its schema.json". |
| `code_error` | Runner code. The message is "the code could not be imported, has no site_analysis, or site_analysis raised an exception". |
| `invalid_transition` | API only (`ReportProgress`). The report asks for an earlier phase. |
| `task_busy` | API only (`ReportProgress`, `ReportResult`). The report lost a race with other reports three times in a row. Send it again. |
| `invalid_result` | Submission refused the result file, and the Agent sends it as the code of a follow-up `EXECUTOR_ERROR`; the message names the field. For a code run, the Agent also uses it when `result.json` is not acceptable (message "the run produced a result file the TRE cannot accept"), or when `site_analysis` returned no valid result (message "site_analysis did not return a valid result"). |
| `result_too_large` | The result file is over 65536 bytes. Set by the Agent's follow-up report after a 400 from Submission. For a code run it is also set when `result.json` is over 65,536 bytes (message "the run produced a result file the TRE cannot accept"), or when the runner reports that the value from `site_analysis` is over 61,440 bytes (message "the result returned by site_analysis is larger than 61,440 bytes"). |
| `result_not_found` | API only (`GetTaskResult`). The task has no result file. |
| `storage_unavailable` | API only. The orchestrator bucket is not reachable. |

## Site setup

### TRE name

Set `TreName` in the Agent's settings to the name of the TRE as registered in Submission. The shipped default is `SAIL`, so every other site must override it. The comparison ignores case.

The Agent does not run a task whose `fed.sites` is not exactly its own `TreName` (a task naming several hospitals is never a hospital's part, so it is not run either). It logs an error, does not read the dataset, and does not report, so the task stays as it is and runs once `TreName` is fixed. If `TreName` is empty, the Agent logs an error and runs no task.

If a TRE is renamed in Submission, its unfinished tasks (`queued`, `running` or `waiting_for_output_approval`) that were enqueued under the old name fail at once with the phase `failed`, `SYSTEM_ERROR` and `tre_renamed`. Enqueue them again under the new name. A change of case only (`My-TRE` to `my-tre`) is not a rename. If the name is changed directly in the database instead, the old tasks are not failed, but they are no longer sent to the Agent.

A TRE that has federated tasks cannot be deleted. The tasks are the record of which results left the TRE.

### Approved images

Set `FederatedTaskSettings:ApprovedImages` to the executor images that this TRE accepts. For example, as an environment variable: `FederatedTaskSettings__ApprovedImages__0=python-runner:1`. A single value also works, with images separated by commas: `FederatedTaskSettings__ApprovedImages=python-runner:1`. A single value replaces any indexed entries, for example those in `appsettings`. Matching is exact and case-sensitive. Entries are trimmed, and blank entries are ignored.

There is no default, because the site approves the images. Only `appsettings.Development*.json` lists `python-runner:1`. If the list is not set, the Agent logs an error. A task that fails the profile is still rejected. Other new tasks are not run or reported. They keep their phase and run when the list is set. A task whose result file is already saved still goes on to output approval. Every federated task runs in the runner image, so a list without it rejects every federated task at this TRE with `image_not_approved`. The Agent cannot tell a deliberate refusal from a typo, so at startup it logs an error when the list is set but has no image named `python-runner` (with any registry prefix and tag).

The input checks run on every poll until the task's result file is saved, so a resumed `running` task is checked again. After the file is saved, the input checks don't run again: whether the result leaves is the decision of output approval, not of the input checks. If its image was removed from the list in the meantime, the Agent logs a warning and the result still goes on to output approval.

### Code scan

Before the dataset is touched, the Agent scans the researcher's code with [Semgrep](https://semgrep.dev) and the hospital's rule file. It is an automatic, explainable refusal on top of the locked-down run (no network, read-only file system, no capabilities; see "Running a code task"), not the only barrier: static analysis of Python can't prove code harmless. No LLM takes part.

The rules belong to the hospital. The Agent ships a default rule file (`Agent/Agent.Api/ScanRules/default.yaml`, version `2026-11-2`): an allowed list of imports, and forbidden calls even through allowed modules (code made from text, starting processes, file access, and back doors in allowed packages). `dev/federated/README.md` ("Code-scan rules") lists what the default rules cover and what they don't, and how to test a rule file. A hospital may use its own file, so the same code may pass at one hospital and be refused at another.

Settings (`FederatedTaskSettings`):

| Setting | Default | Meaning |
|---|---|---|
| `ScanRulesPath` | not set: the default rules next to the app (`ScanRules/default.yaml`) | The hospital's rule file, Semgrep YAML. Its first line must be `# version: <text>`; the Agent reads the version from it. Mount the file and set, for example, `FederatedTaskSettings__ScanRulesPath=/rules/hospital.yaml`. |
| `SemgrepPath` | `/opt/semgrep/bin/semgrep` | The Semgrep the Agent image installs. |
| `ScanTimeoutSeconds` | `30` | After this Semgrep is killed and the task fails. 0 or less fails every scan; the Agent logs an error at startup. |

The Agent writes the code to a temporary file and runs Semgrep on it offline (local rules only, no metrics, no version check). Suppression comments in the code (`# nosemgrep`) are ignored. It reads no data. The outcomes:

- **No findings**: the checks go on to the dataset.
- **Findings, or code that does not parse**: the task is rejected, `rejected_by_input_checks` with `unsafe_code`, and the message names each rule and line (see "Error codes"). Nothing runs and the dataset is not listed or read.
- **Rule file missing, unreadable, without the `# version:` line, or one Semgrep cannot load (a YAML or rule error)**: every task is held, like an unset `ApprovedImages`. It keeps its phase and is checked again at the next poll; the Agent logs an error. A broken config must not reject research.
  Test a rule file with `ScanRules/test.sh` before you deploy it (see `dev/federated/README.md`).
- **Semgrep fails or times out**: the task fails, `SYSTEM_ERROR` with `internal_error` and the message "the code scan could not run". Semgrep's error output is in the Agent log. A broken install is not the researcher's fault, so it is not a rejection.

The version and SHA-256 of the rule file used are saved with the task in `federated-outputs` as `<taskId>.scan.json` at the time of the scan, and sent on every final report as `ScanRulesVersion` and `ScanRulesSha256` (see "Reporting a result"). Submission stores them and shows them as `scan_rules_version` and `scan_rules_sha256` in `GetTask` metadata and per hospital in `GetStudy` and `GetStudyVersion`, so DAAMS and the audit trail see which rules each hospital applied. They are not shown on the Egress review page. Change the version line whenever the file changes; the SHA-256 changes on its own.

Semgrep makes the Agent image larger: about 980 MB instead of about 420 MB. The image installs a pinned Semgrep from PyPI, with hashes, at build time; nothing is downloaded at run time. How hospitals get or build the Agent image is still open (see `dev/federated/README.md`).

### Runner image

`python-runner:1` is the image that a researcher's Python code will run in at the hospital. The researcher's SPE is a notebook that `spms-spe-operator-go` starts from `s2i-generic-data-science-notebook:2025.2` (Open Data Hub 2025b, Python 3.12, plus ACRO with SPMS's thresholds). At the hospital, a version of the same operator on OpenShift will start a locked-down pod from this image. The image must have the same Python and libraries as the SPE notebook; today its base is a 2025a/Python 3.11 placeholder. Code tasks run on it now, so code that works in the SPE (for example code that uses `acro`) may fail at the hospital until the base is moved; that move is still to do (see the runner README). It has no package installers, and one command: `run-analysis <code.py> <dataset dir> <result.json>`. The command checks the dataset against its `schema.json`, calls the researcher's `site_analysis(df)`, checks the return value and writes `result.json`. No error message contains a data value. The Agent starts it for a code task (see "Running a code task"), so `ApprovedImages` must list it. See [`runner/README.md`](../runner/README.md) for the command, the `result.json` format, the error codes, the pod settings and the tests.

### Output approval

Every result waits for a reviewer, so a site must run the Data Egress app (`Egress/Data-Egress-API` and `Egress/Data-Egress-UI`) and connect it to the Agent in both directions. `dev/federated/docker-compose.yml` and its realm files are a working example.

- **Keycloak realm for the Egress app** (`Data-Egress`): realm roles `data-egress-admin` (reviewers) and `dare-tre-admin`; a reviewer user per person with `data-egress-admin`; the clients `Data-Egress-API` and `Data-Egress-UI`. The UI client must put the realm roles in the ID token (a realm-role mapper with "Add to ID token" on, claim `realm_access.roles`). Without it every page of the UI says "access denied".
- **Agent → Egress app.** A confidential client in the Egress realm with a service account that has `dare-tre-admin`, and an audience mapper (type "Audience", included client audience `Data-Egress-API`, added to the access token). The Egress API accepts only tokens whose audience is in its `DataEgressKeyCloakSettings:ValidAudiences`; without the mapper every registration gets 401, and after `EgressUnreachableGiveUpMinutes` the tasks fail. The dev realm's `tre-agent` client is an example. Set the Agent's `FederatedEgressSettings:BaseUrl` to that realm (for example `https://<keycloak>/realms/Data-Egress`), `ClientId` and `ClientSecret` to the client, and `ApiEndpoints:EgressApiUrl` to the Egress API. Without them the Agent holds every result and logs an error once per poll.
- **Egress app → Agent.** A user in the TRE realm with the realm role `data-egress-admin`, stored in the Egress app with `TreCredentials/UpdateCredentials`, and a client in the TRE realm whose tokens carry an audience listed in the Agent's `TreKeyCloakSettings:ValidAudiences`. Give `data-egress-admin` in the TRE realm only to this login: whoever has it can approve results.
- **Egress app settings.** The shipped `appsettings.json` files point at Swansea's Keycloak and at localhost, so set all of these, or the Egress app sends the stored login and its secrets elsewhere:
  - Egress API: `TreKeyCloakSettings:BaseUrl`, `ClientId` and `ClientSecret` (the TRE realm and the client above), `TreAPISettings:Address` (the Agent), and `DataEgressKeyCloakSettings:Authority`, `MetadataAddress`, `BaseUrl`, `ClientId`, `ClientSecret` and `ValidAudiences` (the Egress realm, for reviewers' tokens).
  - Egress UI: `DataEgressKeyCloakSettings` (the Egress realm and the UI client) and `DataEgressAPISettings:Address` (the Egress API).
- **Encryption key.** The Egress API encrypts that stored login with `EncryptionSettings:Key` (a Base64 AES key of 16, 24 or 32 bytes). There is no default; the API does not start without it. Keep it secret, and store the login again if the key changes.
- **Token issuer.** The Agent's `TreKeyCloakSettings:Authority` must equal the `iss` of the tokens the Egress app sends, character for character. If the Agent can't reach the issuer's address, set `TreKeyCloakSettings:MetadataAddress` to one it can reach; the signing keys are fetched from there.

### Agent identity

The Agent must run with the TRE service account: `ConfigInputMethod` is `Upload` for the TRE in the Agent's own database. If it is not, the Agent logs a warning and runs no federated task. The service account is the Keycloak client of the TRE (`Tre.KeycloakClientId`), with the role `dare-tre-admin`.

### Buckets and policies

Agent.Api creates the buckets and policies at startup. Settings are in the `FederatedTaskSettings` section of `Agent/Agent.Api/appsettings.json`. Keys that are not set there use the defaults below.

| Setting | Default |
|---|---|
| `DatasetBucket` | `federated-datasets` |
| `DatasetPrefix` | `dataset/` |
| `AllowedExtensions` | `["csv"]` |
| `UploadPolicyName` | `dataset-upload` |
| `OutputBucket` | `federated-outputs` |
| `OutputReadPolicyName` | `federated-outputs-read` |
| `DatasetUnreachableGiveUpMinutes` | `60` |
| `EgressUnreachableGiveUpMinutes` | `1440` |
| `MaxRunMinutes` | `30` |
| `DefaultCpuCores` | `1` |
| `MaxCpuCores` | `4` |
| `DefaultRamGb` | `8` |
| `MaxRamGb` | `16` |
| `DefaultDiskGb` | `1` |
| `MaxDiskGb` | `4` |
| `MaxConcurrentRuns` | `2` |
| `DockerEndpoint` | `unix:///var/run/docker.sock` |

The last nine settings are for code tasks (see "Running a code task"). `MaxRunMinutes` is the longest a run may take. The Default and Max values are CPU cores, RAM in GB and `/tmp` disk in GB; a request above a max is reduced to it. `MaxConcurrentRuns` is how many code runs this Agent may hold at once; every run still present counts, including one that has exited and is not removed yet. `DockerEndpoint` is the Docker daemon that starts them. At startup the Agent logs an error, and still starts, if `MaxConcurrentRuns` or `MaxRunMinutes` is 0 or less, or if it cannot reach Docker. The values are placeholders until the hospitals confirm their capacity. For a dev stack, see [dev/federated](../dev/federated/README.md).

`AllowedExtensions` sets only which files the upload policy accepts. A list you configure replaces the default. The Agent reads only `.csv` files, whatever this setting holds.

The `dataset-upload` policy allows:

- `s3:ListAllMyBuckets`, `s3:GetBucketLocation`, `s3:GetBucketPolicyStatus` on all buckets.
- `s3:ListBucket`, `s3:ListBucketMultipartUploads` on the dataset bucket.
- `s3:GetObject` on `federated-datasets/dataset/*`.
- `s3:PutObject`, `s3:AbortMultipartUpload`, `s3:ListMultipartUploadParts` on `federated-datasets/dataset/*.csv`, and on `federated-datasets/dataset/*/schema.json` (the schema of a dataset).

It has no delete permission, and no access to `federated-outputs`.

The `federated-outputs-read` policy is for the data custodians. It allows:

- `s3:ListAllMyBuckets`, `s3:GetBucketLocation`, `s3:GetBucketPolicyStatus` on all buckets.
- `s3:ListBucket` on `federated-outputs`.
- `s3:GetObject` on `federated-outputs/*`.

It has no write and no delete permission.

### Give a user access

Link the user to a policy in one of two ways. Use the one your deployment is set up for.

- Add the user to the Keycloak group with the name of the policy: `dataset-upload` for uploaders, `federated-outputs-read` for custodians. RustFS maps the `groups` claim to the policy of the same name.
- Or set the `policy` user attribute to the policy name (the attribute name is `MinioTRESettings:AttributeName`).

Only admins may edit the `policy` attribute. If users can edit their own attributes, they can give themselves any policy. Check this in Keycloak.

### RustFS OIDC settings

Set these on RustFS. Use your own values. Never commit the client secret.

```
RUSTFS_IDENTITY_OPENID_ENABLE=on
RUSTFS_IDENTITY_OPENID_CONFIG_URL=<keycloak>/realms/<realm>
RUSTFS_IDENTITY_OPENID_CLIENT_ID=<client-id>
RUSTFS_IDENTITY_OPENID_CLIENT_SECRET=<client-secret>
RUSTFS_IDENTITY_OPENID_SCOPES=openid,profile,email
RUSTFS_IDENTITY_OPENID_GROUPS_CLAIM=groups
RUSTFS_IDENTITY_OPENID_USERNAME_CLAIM=preferred_username
RUSTFS_IDENTITY_OPENID_EMAIL_CLAIM=email
RUSTFS_IDENTITY_OPENID_REDIRECT_URI=<rustfs>/rustfs/admin/v3/oidc/callback/default
RUSTFS_IDENTITY_OPENID_DISPLAY_NAME=<login button label>
RUSTFS_OUTBOUND_ALLOW_ORIGINS=<keycloak origin, for example http://keycloak:8080>
```

RustFS 1.0.4 blocks outbound calls to private addresses. Without `RUSTFS_OUTBOUND_ALLOW_ORIGINS` (origin only, no path, same host as `CONFIG_URL`), OIDC discovery fails and the log says "discovery blocked by outbound policy".

Two things to check in Keycloak:

- The client needs a `groups` mapper of type Group Membership, with "Full group path" off.
- The `iss` claim in the token must equal the `CONFIG_URL` that RustFS discovers, character for character. Otherwise the console login loops and STS fails with "no OIDC provider configured for issuer". The issuer hostname must be one that the browser, the S3 client and RustFS all reach.

## Orchestrator setup

Submission.Api keeps result files in its own RustFS, not in the TRE and not in the researcher store. Settings are in the section `FederatedResultsStorageSettings`:

| Setting | Default |
|---|---|
| `Url` | none |
| `AccessKey` | none |
| `SecretKey` | none |
| `Bucket` | `federated-results` |
| `AWSRegion` | `us-east-1` |

- Use a separate RustFS. Only Submission.Api may reach it. Do not expose it to TREs or researchers.
- Use a dedicated service account, not the root account. Grant it only these actions:
  - on the bucket `arn:aws:s3:::federated-results`: `s3:CreateBucket`, `s3:GetBucketLocation` (to find or create the bucket), `s3:ListBucket` (so a missing file is reported as missing, not as access denied) and `s3:GetBucketPolicy` (for the startup check below);
  - on the objects `arn:aws:s3:::federated-results/*`: `s3:PutObject` and `s3:GetObject`.

  Submission does not need `s3:ListAllMyBuckets`. Without `s3:GetBucketPolicy` it still works, but logs a warning that it did not check the bucket policy.
- Do not set a bucket policy. Submission logs an error at startup if it finds one.
- Use TLS in production. Plain HTTP is for the local Docker stack only.
- Switch on server-side encryption in RustFS. It is a deployment setting and is not done by the code.
- Keep the keys in secrets or configuration, never in the repository.

Upgrading from inline results: the migration `FederatedResultFiles` drops the old `ResultValue` and `ResultRowCount` columns. A task that was COMPLETE before the migration keeps its state but has no result file, so `GetTaskResult` returns 404 `result_not_found` for it. The values cannot be recovered, not even by a rollback. Deploy Agent.Api and Submission.Api together.

Upgrading to task phases: deploy Submission.Api before (or together with) the Agent. A new Agent gets 404 from an old Submission's `ReportProgress` and does not run tasks. The migration `AddFederatedTaskPhases` fills in the phase of existing rows from their TES state. Input-check errors reported before this change stay `failed`.

Upgrading to study versions: the migration `AddFederatedStudyVersions` deletes every existing federated task, with its phase history, because a task now belongs to a study version and old tasks have none. Their result files stay in `federated-results` but no API call reaches them. `Enqueue` now returns a version ID, and `Cancel` takes one.

TODO: store results in the project's output bucket once study_id → project mapping exists

### Code-task intake settings

What a code task may ask for is set in the section `FederatedTaskSettings`:

| Setting | Default | Meaning |
|---|---|---|
| `ApprovedRunnerImages` | `["python-runner:1"]` | The executor images a code task may use. Exact, case-sensitive match. A configured list replaces the default (for example `FederatedTaskSettings__ApprovedRunnerImages__0=python-runner:2`). An empty list, `"ApprovedRunnerImages": []` in an override file, refuses every code task. The default is set in code, not in `appsettings.json`, because configuration lists merge by index and a shipped list could never be shortened. |
| `MaxCpuCores` | `4` | The largest `resources.cpu_cores`. |
| `MaxRamGb` | `16` | The largest `resources.ram_gb`. |
| `MaxDiskGb` | `20` | The largest `resources.disk_gb`. |
| `StudyTimeoutMinutes` | `10080` (7 days) | A study version's deadline, from intake (see "Studies and versions"). |
| `StudySweepSeconds` | `300` | How often the sweep decides study versions' outcomes. |
| `AggregationTimeoutMinutes` | `1440` (24 hours) | How long a version ready for aggregation may wait before it fails with `aggregation_timeout` (see "Pooling the results"). |

These are Submission's settings. The Agent has its own `MaxCpuCores`, `MaxRamGb` and `MaxDiskGb` (see "Buckets and policies"), with their own defaults (the disk maximum is 4 there, 20 here), and reduces a request to them. Keep Submission's maxima at or below the Agent's. The poll response carries the code of every queued code task, up to 256 KiB each.

The resource defaults are placeholders until the hospitals confirm their capacity. The 256 KiB limit on the code is fixed. Submission refuses to start if a maximum, `StudyTimeoutMinutes`, `StudySweepSeconds` or `AggregationTimeoutMinutes` is 0 or less.

### Aggregator

The aggregator is an Agent.Api deployment with `FederatedTaskSettings:Role = Aggregator` (the default is `Hospital`; any other value stops the Agent at startup). It runs on the research side, not at a hospital, and needs:

- a Keycloak client in the Dare-Control realm whose service account has the realm role `dare-federated-aggregator`, set as `SubmissionKeyCloakSettings` (`ConfigInputMethod` `Upload`, `ClientId`, `ClientSecret`);
- `ApiEndpoints:SubmissionApiUrl`;
- `FederatedTaskSettings:ApprovedImages` (required in this role), `DockerEndpoint`, `MaxRunMinutes`, `MaxConcurrentRuns` and the run limits, as a hospital Agent;
- its own databases (Hangfire must not share a hospital Agent's).

It skips the hospital-only setup: no dataset or output buckets, no Egress, no code scan, no `TreName`. It has the same security note as a hospital Agent: the Docker socket is root on its host; production moves to the research environment's SPE operator, as hospitals move to theirs.

Every container and volume carries the label `fed.owner` (`hospital` or `aggregator`), so a hospital Agent and the aggregator on one Docker daemon never sweep or count each other's runs. Runs from before the label belong to the hospital. Still run at most one Agent of each role per daemon.

## Security

Trust boundaries:

- The TRE trusts only its own RustFS, outgoing HTTPS to Submission.Api, and the hospital's Data Egress app. Nothing outside the hospital calls into the TRE. The Egress app calls the Agent's `EgressResults` with the decision.
- The orchestrator RustFS is reachable only by Submission.Api.

Who can read what:

- Data custodians read the TRE copy in `federated-outputs`, with `federated-outputs-read`. Dataset uploaders cannot.
- A `dare-control-admin` user reads results through `GetTaskResult`.
- Only the Agent service account of a TRE can report, and only for the tasks of that TRE.
- The Agent uses its existing TRE storage credentials for `federated-outputs`. Submission uses its own service account for `federated-results`.

Integrity:

- The Agent releases a result only if its SHA-256 equals the one the reviewer approved (`object.sha256` in the review record), so `result_sha256` is the SHA-256 shown on the review page.
- Submission computes the SHA-256 of the received bytes and stores it as `result_sha256`. The Agent compares it with the SHA-256 of its own copy before it marks the copy as released.
- The TRE copy then has the metadata `released-key` and `released-sha256`. A custodian can show that the released file is byte for byte the TRE copy. The tag can be missing on a released file (see "Reporting a result"). Without it, compare the copy with `result_sha256` from `GetTask`.
- `GetTaskResult` checks the stored file against `result_sha256` on every read.
- Submission logs each accepted release (task id, TRE, size, SHA-256) and never the content.

Known risks:

- There is no automatic disclosure control. A reviewer approves each result, but nothing flags a result based on very few people, or one that gives the exact value of one person. The reviewer must catch it.
- Error reports are not reviewed. Error reports (`EXECUTOR_ERROR`, `SYSTEM_ERROR`, input-check rejections) leave without review. The researcher's code can put data in an exception or in its output, so the Agent never forwards text the code produced: the orchestrator gets a fixed code and message, and the details stay in the TRE logs. The cost is that a researcher sees only the kind of failure (see "Running a code task"). The container's stdout and stderr are never kept.
- The code scan is not complete. Static analysis of Python can't catch every way to reach the network, files or the system, so code that gets past it still runs only in the locked-down container. `dev/federated/README.md` lists the known gaps of the default rules.
- The Agent has the Docker socket, which is root-equivalent on the host, and `/outputs` has no size limit beyond the per-file cap. Both are PoC limits (see "Running a code task").
- Orchestrator admins are trusted not to change stored results or hashes, until signed results are added.
- Any `dare-control-admin` user can read the results of every study.
- Orphan files. Submission writes the file before it saves the `released` phase. If the row save fails, or a concurrent report wins, the file stays in the orchestrator bucket with no row that points to it. The key holds the SHA-256, so it never overwrites another result. No API call can reach an orphan. An orchestrator admin can find and delete orphans in the bucket.

TODO: researcher access scoped by study once study_id → project mapping exists

Planned follow-ups:

- Signed results. Each Agent signs the result bytes with its own key, and Submission verifies and stores the signature. This gives a result that does not depend on trusting the orchestrator.
- Study details, researcher and data permit on the review page, and the permit checks, from the 5S-Crate (task 4.1).
- A rule for the TRE name in `SaveTre`.
- Separate storage credentials for the Agent.

## Uploader guide

1. Log in to the RustFS console with your TRE Keycloak account.
2. Open the bucket `federated-datasets`.
3. Use one folder per dataset under `dataset/`. Name it with the GOV-12 dataset name, for example `dataset/cohort_2024/`.
4. Put exactly one lowercase `.csv` file in the folder.
5. To replace a dataset, upload a file with the same name. It overwrites the old one.
6. Numbers use `.` as the decimal point, with no thousands separators. A value that is not of its column's type in `schema.json` fails with `schema_mismatch`.
7. Only files directly in the dataset folder count. Subfolders are ignored.
8. Each column name must be unique. Names are case-sensitive, so `age` and `Age` are different columns.
9. Put a `schema.json` in the same folder, next to the `.csv`. The Agent rejects a task with `schema_not_found` without it. It lists each column in the file's order, with its `name` and its `type`: `integer`, `number`, `string` or `boolean`:

   ```json
   {"columns": [{"name": "age", "type": "integer"}, {"name": "sex", "type": "string"}]}
   ```

   The runner checks the CSV against it (same names, same order, every value of its type). See [`runner/README.md`](../runner/README.md) for the rules. The upload policy allows a file with exactly the name `schema.json` in a dataset folder, as `dataset/<name>/schema.json`, and no other file besides the `.csv`.

You cannot delete files. Ask a RustFS admin if one must be removed. If a folder holds two `.csv` files, tasks fail with `ambiguous_dataset`.

## Deferred SQL datasets

SQL dump datasets are not supported yet. The plan for later:

- A dataset folder may hold a `.sql` dump instead of a CSV. It must be data only: one `CREATE TABLE` and `INSERT INTO ... VALUES` statements.
- Agent.Api checks every statement before it runs any. Any other statement rejects the whole file with a new code, `invalid_sql_dump`.
- The dump loads into an isolated in-memory SQLite database. Uploaded SQL never runs against a real database, and never as a query.
- A plain `pg_dump` will not pass. Export with `--inserts` and remove the `SET` and `SELECT` lines.
- To turn it on: add `sql` to `FederatedTaskSettings:AllowedExtensions` (this also widens the upload policy) and add a `.sql` branch to the dataset reader.

## Known issue: RustFS console loop

The RustFS console loops for non-admin users (rustfs/console#271). The fix is in console v0.1.38. The RustFS 1.0.4 release build pins console v0.1.37, so it does not have the fix. It does not arrive by itself: before you upgrade RustFS, check that the release notes or the build show console v0.1.38 or later. Storage permissions are not affected.

Until then, custodians and uploaders use an S3 client with their Keycloak login (STS) instead of the console.
