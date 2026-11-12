# Federated tasks

The orchestrator (Submission.Api) queues a GA4GH TES task for one TRE. The TRE Agent (Agent.Api) polls for it, reads a CSV dataset from the TRE bucket, runs `count` or `mean`, holds the result until a hospital reviewer approves it in the Data Egress app, and then reports the result.

There is no DAAMS signing or approval gate on the orchestrator side yet.

To try the flow locally, see [dev/federated](../dev/federated/README.md). It is a Docker Compose stack with both APIs, the Data Egress app, Keycloak, RustFS and a sample dataset.

## Flow

1. A `dare-control-admin` user calls `POST /api/FederatedTask/Enqueue` with a TES task. Submission validates it against the federated profile. The task is stored at the step `queued`. The response holds the task id.
2. The Agent job `Run Federated Tasks` (`JobSettings:FederatedTaskJobName`) runs every `JobSettings:federatedTaskSchedule` minutes. It calls `GET /api/FederatedTask/GetPendingTasksForTre`. The TRE is taken from the token.
3. The Agent checks the task against the profile, then runs the hospital's input checks in this order: the executor image is in `ApprovedImages` (see "Approved images"), and the one `.csv` under `dataset/<name>/` is in the dataset bucket. It reads no data values yet. If a check fails, or there is no file or more than one, the task is rejected (`rejected_by_input_checks`). If `ApprovedImages` is not set, the task still gets the profile check, but the hospital's checks do not run. The task is not run or reported, and keeps its step. A `queued` task rejected here never reaches `running`. A resumed `running` task is checked again, so if its dataset was removed in the meantime it is rejected with a `running_at` in its history. If the dataset bucket cannot be listed, the task keeps its step and is tried again at the next poll, until `DatasetUnreachableGiveUpMinutes` (default 60) have passed since it was queued, or since the Agent started if that is later. A task that waited while the Agent was down, or while `ApprovedImages` was not set, so gets the full time. Each restart of the Agent gives pending tasks the full time again. After that it fails with `SYSTEM_ERROR` and `internal_error`, so a bucket that is wrongly configured or denied does not leave tasks queued for good.
4. For a `queued` task, the Agent calls `POST /api/FederatedTask/ReportProgress` with the step `running`. Submission moves the task to `running`. Then the Agent reads the file. If the bucket fails during the read, the task stays `running` and is resumed at the next poll, with the same `DatasetUnreachableGiveUpMinutes` limit as a failed listing. A file that is not valid CSV is reported at once (`invalid_dataset`).
5. The Agent runs the operation in-process. It does not start a container. The result is a JSON file (see "Task state and result").
6. The Agent saves the file in the TRE bucket `federated-outputs` as `<taskId>.json`. If the file is already there from an earlier poll, the Agent reuses it and does not compute again.
7. The Agent registers the result with the Data Egress app, then calls `ReportProgress` with the step `waiting_for_output_approval`. The task is `PAUSED`. Nothing has left the TRE. See "Output approval".
8. A hospital reviewer opens the request in the Egress app. The page shows the study ID, the code that ran (image and command) and the result in full, with its SHA-256. The reviewer approves, or rejects with a reason. The Egress app sends the decision to the Agent, which records it in `federated-outputs/<taskId>.review.json`.
9. At the next poll the Agent acts on the decision. On approval it hashes the saved file again. If the hash equals the reviewed SHA-256, it releases the file: `POST /api/FederatedTask/ReportResult` with the file as an upload. Submission checks the caller and the file, and stores it in the orchestrator bucket `federated-results`. The task moves to `released`. On rejection the Agent reports `CANCELED` with `RejectedAt` `output_approval`, the code `rejected_by_reviewer` and the reviewer's reason as the message, and no file. If the saved file no longer matches the reviewed one, or is gone, the Agent reports `SYSTEM_ERROR` with `result_changed_after_review` and no file.
10. After a release, the Agent compares the SHA-256 that Submission stored with the SHA-256 of its own copy. If they match, it adds the metadata `released-key` and `released-sha256` to the TRE copy.
11. A `dare-control-admin` user calls `GET /api/FederatedTask/GetTask?id=<taskId>` for the step, the state and where the result is stored, and `GET /api/FederatedTask/GetTaskResult?id=<taskId>` for the file.

If the Agent cannot save the file, or the registration with the Egress app fails, or the release fails (network error, 5xx, or a 4xx that is not listed under "Reporting a result"), the task stays `running` and the Agent tries again at the next poll. A task that fails before there is a file (for example `dataset_not_found`) is reported as a rejection or a failure, with no file, and does not go through save and release.

`GetPendingTasksForTre` returns the tasks that are not finished: `running` first, then `queued`, then `waiting_for_output_approval`, oldest first inside each group. For a `waiting_for_output_approval` task the Agent reads the review record and acts on the decision, if there is one (see "Output approval"). A `running` task was marked by an earlier poll that did not finish. The Agent resumes it, and does not report `running` again. If the result file from the earlier poll is in the TRE bucket, the Agent reuses it. Otherwise it runs the task again from the start, so every step the Agent does before it reports must be safe to do twice. The Agent skips, and logs, a pending task in any state other than `QUEUED`, `RUNNING` or `PAUSED`.

`GetPendingTasksForTre`, `ReportProgress` and `ReportResult` accept only the TRE Agent's service account (role `dare-tre-admin`). See "Reporting a result". `Enqueue`, `GetTask` and `GetTaskResult` need `dare-control-admin`.

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
    Sub-->>Admin: task id (step queued)
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
    Agent->>Sub: POST ReportProgress (step running), only for a queued task
    Sub-->>Agent: 200, task is running (409 if the task is past running)
    Agent->>TS: read dataset CSV
    Agent->>Agent: compute result, build result JSON
    alt success
        Agent->>TS: save federated-outputs/taskId.json (once, reused on retry)
        Agent->>TS: save taskId.review.json (pending, result sha256)
        Agent->>Egress: POST AddNewDataEgress (study, code, result bytes, sha256)
        Agent->>Sub: POST ReportProgress (step waiting_for_output_approval)
        Reviewer->>Egress: approve, or reject with a reason
        Egress->>Agent: POST EgressResults (decision, reviewer, time, reason, sha256)
        Agent->>TS: save decision in taskId.review.json
        Note over Agent: next poll
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
            Sub-->>Agent: 503 storage_unavailable, task keeps its step, retried next poll
        else stored
            Sub->>OS: put treId/taskId/sha256.json
            Sub->>Sub: save step released + bucket/key/sha256
            Sub-->>Agent: 200 + result_key/result_sha256
            Agent->>Agent: check result_sha256 equals own sha256
            Agent->>TS: tag TRE copy with released-key / released-sha256
        end
    else input check fails (e.g. dataset_not_found)
        Agent->>Sub: POST ReportResult (CANCELED, RejectedAt input_checks, error_code, error_message, no file)
        Sub-->>Agent: 200, step rejected_by_input_checks, no file written
    else task fails (e.g. column_not_found)
        Agent->>Sub: POST ReportResult (EXECUTOR_ERROR or SYSTEM_ERROR, error_code, no file)
        Sub-->>Agent: 200, step failed, no file written
    end
    Admin->>Sub: GET GetTask
    Sub-->>Admin: state + step + step times + result_bucket/result_key/result_sha256
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
| 400 | `invalid_task`, or any profile code from the table below (for example `unknown_operation`) | The request or the task is not valid. This includes a form with an unknown `State` value or a bad `ErrorCode`, and a request over the size limit of `ReportResult` (73728 bytes). |
| 400 | `invalid_result` | `ReportResult`: the result file is not valid, or does not match the task. |
| 400 | `result_too_large` | `ReportResult`: the result file is over 65536 bytes. |
| 404 | `task_not_found` | No such task. `ReportProgress` and `ReportResult` also return this for a task that belongs to another TRE. |
| 404 | `result_not_found` | `GetTaskResult`: the task is not `released`. |
| 409 | `task_already_reported` | `ReportResult` or `ReportProgress` for a task that is already final. When the task is `released`, the body also has `result_sha256` and `result_key`. |
| 409 | `invalid_transition` | `ReportProgress` asks to go back to an earlier step. |
| 409 | `task_busy` | `ReportProgress` or `ReportResult` lost a race with other reports for the same task three times in a row. Nothing was recorded. Send the report again. |
| 500 | `internal_error` | `GetTaskResult`: the stored file does not match its SHA-256. The file is not returned. |
| 503 | `storage_unavailable` | The orchestrator bucket cannot be reached, or `ReportResult` lost a race and could not read the task again. For `ReportResult` the task keeps its step and the Agent sends the report again at the next poll. |

`result_not_found` and `storage_unavailable` are returned by the API only. An Agent never reports them as a task result.

401 and 403 come from authentication and have no body. `ReportProgress`, `ReportResult` and `GetPendingTasksForTre` return 403 to any caller that is not a TRE Agent service account.

A `ReportResult` with state `EXECUTOR_ERROR`, `SYSTEM_ERROR` or `CANCELED` must have an `ErrorCode`, or it gets a 400. Any code that matches `^[a-z_]{1,64}$` is accepted, not only the ones listed below. `ErrorMessage` is at most 512 characters and has no control characters.

### Reporting progress

`POST /api/FederatedTask/ReportProgress` takes a JSON body:

```json
{ "task_id": "0b9a3c4e-6f1d-4c1e-9a52-3d7b8e2f1a10", "step": "running" }
```

`step` is `running` or `waiting_for_output_approval`. Any other value, or a `task_id` that is not a GUID, gets 400 `invalid_task`. A 200 response is `{ "step": "<the step the task is at now>" }`.

- The task is at an earlier step: it moves to the new step and the time is recorded.
- The task is already at that step: 200, nothing changes. The Agent can send the same report again.
- The task is at a later step: 409 `invalid_transition`.
- The task is final: 409 `task_already_reported`.
- Other reports for the same task won the race three times in a row: 409 `task_busy`. Nothing changed. Send the report again.
- The task belongs to another TRE, or does not exist: 404 `task_not_found`.

The Agent sends `running` when it picks a `queued` task up, and `waiting_for_output_approval` after it has registered the result with the Egress app.

### Reporting a result

`POST /api/FederatedTask/ReportResult` is `multipart/form-data`.

| Field | Rule |
|---|---|
| `TaskId` | The task GUID. |
| `State` | `COMPLETE`, `CANCELED`, `EXECUTOR_ERROR` or `SYSTEM_ERROR`. |
| `RejectedAt` | `CANCELED` only, and required there: `input_checks` or `output_approval`. Any other value is 400 `invalid_task`. It is not allowed with another state. |
| `ErrorCode`, `ErrorMessage` | Error states and `CANCELED`. See the limits above. A rejection (`CANCELED`) also needs a non-empty `ErrorMessage`. |
| `DatasetSha256` | Error states and `CANCELED`. 64 lowercase hex characters. On `COMPLETE` Submission takes it from the result file. |
| `result` (file) | Required for `COMPLETE`. Not allowed for `CANCELED` or an error state. One file only. |

A report is accepted from any step that is not final, so a `queued`, `running` or `waiting_for_output_approval` task can be finished. `COMPLETE` moves the task to `released`. `CANCELED` moves it to `rejected_by_input_checks` or `rejected_at_output_approval`, as `RejectedAt` says. `EXECUTOR_ERROR` and `SYSTEM_ERROR` move it to `failed`.

Submission checks the caller first. Only the Agent service account of a TRE is accepted: the token `azp` is the TRE's Keycloak client id, and `preferred_username` is `service-account-<clientId>`, with role `dare-tre-admin`. Anyone else gets 403, whatever the request holds. The TRE comes from the token, never from the form or the file. A request over 73728 bytes is rejected by the framework with 400 `invalid_task`, before this check.

The `result` file:

- It is at most 65536 bytes, or the answer is 400 `result_too_large`.
- It is UTF-8 JSON, nested at most 16 levels, with no duplicate property names. It must match the result document and the task (`taskId`, `tre`, `studyId`, `operation`, `dataset`), or the answer is 400 `invalid_result`. The message names the field and never repeats the content.
- The file name of the upload is ignored. Submission builds the storage key itself.

A 200 response has `result`, `result_bucket`, `result_key` and `result_sha256`. The last three are only for `COMPLETE`.

If Submission cannot write the file, it answers 503 `storage_unavailable` and the task keeps its step. If the Agent gets 400 `invalid_result` or `result_too_large`, it sends a follow-up `EXECUTOR_ERROR` report with the same code. A 409 for a `released` task carries `result_sha256` and `result_key`, because the Agent cannot call `GetTask`. The Agent compares the sha256 with its own copy and, if they are equal, tags the TRE copy with the key. The Agent only gets this 409 when two reports for the same task race. It does not ask again later.

The tag can be missing on a released file. This happens when the Agent does not get the 200 (a timeout after Submission saved the result, or an Agent restart), or when tagging fails. Submission then has the task as `released` and never gives it to the Agent again, so nothing adds the tag later. A missing tag does not mean the file was not released. To check, compare the TRE copy's SHA-256 with `result_sha256` from `GetTask`.

TODO: re-tag released TRE copies that have no `released-key` (follow-up ticket).

## Output approval

Every computed result is reviewed at the hospital before it leaves the TRE. The review happens in the Data Egress app (`Egress/`, forked from Swansea's DARE-Control). Native 5S-TES outputs are reviewed in the same app.

Registering. After the Agent saves the result, it writes the review record (below) as pending, then calls the Egress API's `POST /api/DataEgress/AddNewDataEgress` with the task ID as `SubmissionId`, the kind `federated`, the study ID, the image and command, the result bytes and their SHA-256. The Egress app keeps its own copy of the bytes, so the reviewer reads exactly what was registered, and the Egress app needs no access to `federated-outputs`. A second registration of the same task is accepted as already done. Then the Agent marks the record as registered and reports `waiting_for_output_approval`. If a step fails, the task keeps its step and the next poll goes on from where it stopped.

The Agent calls the Egress API with a client-credentials token of its own Keycloak client in the Egress realm (`DataEgressKeyCloakSettings:ClientId` and `ClientSecret`; the service account has the role `dare-tre-admin`), at `ApiEndpoints:EgressApiUrl`. If the client ID or secret is not set, the Agent does not register the result. It logs an error once per poll and the task keeps its step.

Deciding. The reviewer sees the study ID, the code and the result with its SHA-256. The study details and the data permit are not provided yet; they come with the 5S-Crate (task 4.1). A rejection needs a reason. The Egress app saves the decision first, then calls the Agent's `POST /api/Submission/EgressResults` with the reviewer, the decision time, the reason and the SHA-256 of the bytes the reviewer saw. If that call fails, the reviewer sends the request again; the Egress app then sends the stored decision, not the form.

For a federated task, `EgressResults` accepts the decision only from the Egress app's own login (role `data-egress-admin`), not from `dare-tre-admin`. It returns 404 for a task with no review record, 400 for a decision with no reviewer, time or SHA-256, or a rejection without a reason, and 409 for a SHA-256 that differs from the registered one or a decision that differs from one already recorded. The same decision sent again is accepted with no change. It only records the decision; the next poll releases or rejects. A `SubId` that is not a GUID runs the native code as before. A GUID with no review record gets 404 and never runs the native code.

The Egress app logs in to the TRE realm with a user that has `data-egress-admin` (stored with `TreCredentials/UpdateCredentials`), with a client whose tokens carry an audience the Agent accepts (`TreKeyCloakSettings:ValidAudiences`).

### Review record

The Agent keeps the review in the TRE bucket next to the result, as `federated-outputs/<taskId>.review.json`. It survives a restart and is readable by custodians with `federated-outputs-read`. It is shaped as a 5S-Crate 0.4 `DisclosureCheck`:

```json
{
  "@type": "AssessAction",
  "additionalType": "https://w3id.org/shp#DisclosureCheck",
  "actionStatus": "http://schema.org/CompletedActionStatus",
  "object": { "sha256": "ae28cc6d7117376e2245e59e139d2a58cea8b65d5bf69de921210decfa84054b" },
  "registered": true,
  "startTime": "2026-11-11T22:54:09.8825703+00:00",
  "endTime": "2026-11-11T22:54:35.9042277+00:00",
  "agent": "reviewer",
  "description": null
}
```

- `actionStatus` is `PotentialActionStatus` while waiting, `CompletedActionStatus` on approval and `FailedActionStatus` on rejection.
- `object.sha256` is the SHA-256 of the registered result. Only a file with this hash is released.
- `registered` is `false` until the Egress app has accepted the registration.
- `startTime` is the registration time, `endTime` the decision time, and `agent` the reviewer.
- `description` holds the reason. schema.org defines it; the 5S-Crate profile does not, so it is our extension.

Once a result has a review record, it is never computed again. If the saved result is gone, a rejection is still reported as a rejection, and anything else as `result_changed_after_review`.

## The federated TES profile

| TES field | Rule |
|---|---|
| `executors` | Exactly 1. `image` must be `5s-tes/federated-ops:1`. |
| `executors[0].command` | `[<operation>, ...args]`. The operation is `count` or `mean`. |
| command args | `--column <name>`: at most once. Required for `mean`, optional for `count`. `--filter <column>:<op>:<value>`: repeatable. |
| filter `<op>` | `eq`, `gt`, `lt`, `gte` or `lte`. Split on the first two `:` only, so a value may contain `:`. |
| `inputs` | Exactly 1. `url` is `dataset://<name>`. |
| `outputs` | Must be null or empty. The result is returned in the task log. |
| `tags` | `tres` names exactly one existing TRE. It is trimmed and lowercased, and must not contain `\|`. `study_id` must not be empty. Both are at most 1024 characters, the limit for strings in the result document. |
| `resources`, `volumes`, `name`, `description` | Accepted and ignored. |

The dataset name must match `^[a-z0-9][a-z0-9_-]{0,62}$`.

Example: the mean of `age` on dataset `cohort_2024`, for rows where `sex` is `F`.

```json
{
  "executors": [
    {
      "image": "5s-tes/federated-ops:1",
      "command": ["mean", "--column", "age", "--filter", "sex:eq:F"]
    }
  ],
  "inputs": [
    {
      "url": "dataset://cohort_2024",
      "path": "/data/cohort_2024",
      "type": "DIRECTORY"
    }
  ],
  "outputs": [],
  "tags": {
    "tres": "my-tre",
    "study_id": "study-001"
  }
}
```

### Task steps and state

A task has a step. It says where the task is, from intake to release. `GetTask` returns a valid TES task, so `state` is always a standard TES state. The step is in the metadata.

| Step | TES `state` | When |
|---|---|---|
| `queued` | `QUEUED` | The task is enqueued. |
| `running` | `RUNNING` | The Agent has picked the task up. |
| `waiting_for_output_approval` | `PAUSED` | The result is saved in the TRE and waits for a reviewer's decision in the Egress app. Set by the Agent. |
| `released` | `COMPLETE` | The result file is stored in the orchestrator. |
| `rejected_by_input_checks` | `CANCELED` | The task was refused before it ran. The error code is one of `invalid_task`, `unknown_operation`, `invalid_filter`, `image_not_approved`, `invalid_dataset_name`, `dataset_not_found` or `ambiguous_dataset`. |
| `rejected_at_output_approval` | `CANCELED` | A reviewer refused the result. The error code is `rejected_by_reviewer` and the message is the reviewer's reason. |
| `failed` | `EXECUTOR_ERROR` or `SYSTEM_ERROR` | Any other error. The Agent sends the state. `SYSTEM_ERROR` is for `internal_error`, `tre_renamed` and `result_changed_after_review`. `EXECUTOR_ERROR` is for the other codes. |

A task only moves forward: `queued`, `running`, `waiting_for_output_approval`, then one final step (`released`, a `rejected_*` step or `failed`). A final step never changes. The Agent decides between a rejection and a failure by the error code.

The result is in `logs[0].metadata`. Every key and value is a string.

- `step`: the current step, as in the table.
- `<step>_at` for each step the task has reached, for example `queued_at` and `running_at`. The value is a UTC time in ISO 8601 form (`O` format). A task rejected before it ran has no `running_at`. `logs[0].start_time` is `running_at`. `logs[0].end_time` is the time of the final step.
- On `released`: `result_bucket`, `result_key` and `result_sha256` (where the result file is stored in the orchestrator, and its SHA-256). On a rejection or a failure: `error_code` and `error_message`.

- The computed values (count, mean) are in the result file, not in the metadata.
- `dataset_sha256` is added whenever the Agent read the dataset file, on success and on errors raised after the read (including `invalid_dataset`). It is the lowercase hex SHA-256 of the exact bytes of the CSV. It is absent when the file was not read: every input-check rejection (`invalid_task`, `unknown_operation`, `invalid_filter`, `image_not_approved`, `invalid_dataset_name`, `dataset_not_found`, `ambiguous_dataset`), `tre_renamed`, and `internal_error` before the read. An empty file gives `invalid_dataset` with the hash.

### How the steps fit the standards

- **GA4GH TES.** `GetTask` always returns a valid TES task with a standard TES `state`. The step, `error_code` and `error_message` are in `logs[0].metadata`, which TES leaves free-form. TES defines no error codes, so ours clash with nothing. `PAUSED` for `waiting_for_output_approval` fits: in TES a task can be paused, and support for it is optional.
- **`CANCELED` for a rejection is a stretch.** TES says `CANCELED` means the user canceled the task. A hospital that refuses a task is not that, but TES has no "rejected" state. `SYSTEM_ERROR` and `EXECUTOR_ERROR` say that something broke, and a refusal is a decision, not a fault. `CANCELED` is the closest: the task stopped on purpose, the state is final, and TES clients don't retry it. A generic TES client that reads only `state` can tell the researcher that they canceled the task. A client that reads the metadata sees the real step and reason. DAAMS should read the status from the metadata (task 4.3).
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
  "operation": "mean",
  "dataset": "cohort_2024",
  "datasetSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "completedAt": "2026-11-05T09:30:00+00:00",
  "result": {
    "kind": "scalar",
    "value": 41.5,
    "rowCount": 120
  }
}
```

`result.value` is a decimal. It is `null` when there are no rows. `result.kind` is always `scalar` for now.

The file is stored under the key `<treId>/<taskId>/<sha256>.json` in the orchestrator bucket `federated-results`. `treId` is the TRE id in Submission and `sha256` is the SHA-256 of the file.

### Reading the result

`GET /api/FederatedTask/GetTaskResult?id=<taskId>` needs `dare-control-admin`. It reads the file from the orchestrator bucket, checks its SHA-256 against the one on the task, and returns it as the attachment `<taskId>.json` with `X-Content-Type-Options: nosniff`. The errors are in the table above: 404 `task_not_found` or `result_not_found`, 503 `storage_unavailable`, and 500 `internal_error` when the file fails the check.

TODO: researcher access scoped by study once study_id → project mapping exists

### Error codes

| Code | Meaning |
|---|---|
| `invalid_task` | The task does not follow the profile. The message says which rule failed. |
| `image_not_approved` | The executor image is not in this TRE's `ApprovedImages`. The message names the image. |
| `invalid_dataset_name` | The name does not match `^[a-z0-9][a-z0-9_-]{0,62}$`. |
| `dataset_not_found` | No `.csv` file under `dataset/<name>/`. |
| `ambiguous_dataset` | More than one `.csv` file under `dataset/<name>/`. |
| `invalid_dataset` | The file is empty, is not valid CSV (for example a stray `"` inside a field), or has two columns with the same name. The message gives the line number or the column name, never the data. |
| `unknown_operation` | The operation is not `count` or `mean`. |
| `invalid_filter` | The operator is not `eq`, `gt`, `lt`, `gte` or `lte`, or the filter is not `<column>:<op>:<value>`. |
| `column_not_found` | The column, or a filter column, is not in the dataset. |
| `non_numeric_value` | `mean` found a value that is not a number. |
| `internal_error` | An unexpected failure, for example storage is unreachable. The message is generic, so that bucket names and hosts do not leave the TRE. The details are in the Agent log. |
| `tre_renamed` | The TRE was renamed in Submission before the task finished. Set by Submission, not the Agent. Enqueue the task again under the new name. |
| `rejected_by_reviewer` | A hospital reviewer rejected the result in the Egress app (step `rejected_at_output_approval`, state `CANCELED`). The message is the reviewer's reason, at most 512 characters, with control characters removed. No file is released. |
| `result_changed_after_review` | The result was approved, but the saved file in `federated-outputs` no longer has the SHA-256 the reviewer saw, or is gone (state `SYSTEM_ERROR`). Nothing is released. |
| `invalid_transition` | API only (`ReportProgress`). The report asks for an earlier step. |
| `task_busy` | API only (`ReportProgress`, `ReportResult`). The report lost a race with other reports three times in a row. Send it again. |
| `invalid_result` | Submission refused the result file. The Agent sends it as the code of a follow-up `EXECUTOR_ERROR`. The message names the field. |
| `result_too_large` | The result file is over 65536 bytes. Set by the Agent's follow-up report after a 400 from Submission. |
| `result_not_found` | API only (`GetTaskResult`). The task has no result file. |
| `storage_unavailable` | API only. The orchestrator bucket is not reachable. |

## Site setup

### TRE name

Set `TreName` in the Agent's settings to the name of the TRE as registered in Submission. The shipped default is `SAIL`, so every other site must override it. The comparison ignores case.

The Agent does not run a task whose `tres` is not its own `TreName`. It logs an error, does not read the dataset, and does not report, so the task stays as it is and runs once `TreName` is fixed. If `TreName` is empty, the Agent logs an error and runs no task.

If a TRE is renamed in Submission, its unfinished tasks (`queued`, `running` or `waiting_for_output_approval`) that were enqueued under the old name fail at once with the step `failed`, `SYSTEM_ERROR` and `tre_renamed`. Enqueue them again under the new name. A change of case only (`My-TRE` to `my-tre`) is not a rename. If the name is changed directly in the database instead, the old tasks are not failed, but they are no longer sent to the Agent.

A TRE that has federated tasks cannot be deleted. The tasks are the record of which results left the TRE.

### Approved images

Set `FederatedTaskSettings:ApprovedImages` to the executor images that this TRE accepts. For example, as an environment variable: `FederatedTaskSettings__ApprovedImages__0=5s-tes/federated-ops:1`. A single value also works, with images separated by commas: `FederatedTaskSettings__ApprovedImages=5s-tes/federated-ops:1`. A single value replaces any indexed entries, for example those in `appsettings`. Matching is exact and case-sensitive. Entries are trimmed, and blank entries are ignored.

There is no default, because the site approves the images. Only `appsettings.Development*.json` lists `5s-tes/federated-ops:1`. If the list is not set, the Agent logs an error. A task that fails the profile is still rejected. Other new tasks are not run or reported. They keep their step and run when the list is set. A task whose result file is already saved still goes on to output approval. Every federated task uses `5s-tes/federated-ops:1` today, so a list without it rejects every federated task at this TRE with `image_not_approved`. The Agent cannot tell a deliberate refusal from a typo or a registry prefix, so at startup it logs an error when the list is set but does not include that image.

The input checks run on every poll until the task's result file is saved, so a resumed `running` task is checked again. After the file is saved, the input checks don't run again: whether the result leaves is the decision of output approval, not of the input checks. If its image was removed from the list in the meantime, the Agent logs a warning and the result still goes on to output approval.

### Output approval

Every result waits for a reviewer, so a site must run the Data Egress app (`Egress/Data-Egress-API` and `Egress/Data-Egress-UI`) and connect it to the Agent in both directions. `dev/federated/docker-compose.yml` and its realm files are a working example.

- **Keycloak realm for the Egress app** (`Data-Egress`): realm roles `data-egress-admin` (reviewers) and `dare-tre-admin`; a reviewer user per person with `data-egress-admin`; the clients `Data-Egress-API` and `Data-Egress-UI`. The UI client must put the realm roles in the ID token (a realm-role mapper with "Add to ID token" on, claim `realm_access.roles`). Without it every page of the UI says "access denied".
- **Agent → Egress app.** A confidential client in the Egress realm with a service account that has `dare-tre-admin`. Set the Agent's `DataEgressKeyCloakSettings:ClientId` and `ClientSecret` to it, and `ApiEndpoints:EgressApiUrl` to the Egress API. Without them the Agent holds every result and logs an error once per poll.
- **Egress app → Agent.** A user in the TRE realm with the realm role `data-egress-admin`, stored in the Egress app with `TreCredentials/UpdateCredentials`, and a client in the TRE realm whose tokens carry an audience listed in the Agent's `TreKeyCloakSettings:ValidAudiences`. Give `data-egress-admin` in the TRE realm only to this login: whoever has it can approve results.
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

`AllowedExtensions` sets only which files the upload policy accepts. A list you configure replaces the default. The Agent reads only `.csv` files, whatever this setting holds.

The `dataset-upload` policy allows:

- `s3:ListAllMyBuckets`, `s3:GetBucketLocation`, `s3:GetBucketPolicyStatus` on all buckets.
- `s3:ListBucket`, `s3:ListBucketMultipartUploads` on the dataset bucket.
- `s3:GetObject` on `federated-datasets/dataset/*`.
- `s3:PutObject`, `s3:AbortMultipartUpload`, `s3:ListMultipartUploadParts` on `federated-datasets/dataset/*.csv`.

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

Upgrading to task steps: deploy Submission.Api before (or together with) the Agent. A new Agent gets 404 from an old Submission's `ReportProgress` and does not run tasks. The migration `AddFederatedTaskSteps` fills in the step of existing rows from their TES state. Input-check errors reported before this change stay `failed`.

TODO: store results in the project's output bucket once study_id → project mapping exists

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

- There is no automatic disclosure control. A reviewer approves each result, but nothing flags a narrow filter that gives a `count` of 1 or the exact value of one person in a `mean`. The reviewer must catch it.
- Error reports are not reviewed. Error reports (`EXECUTOR_ERROR`, `SYSTEM_ERROR`, input-check rejections) leave without review. Today that is safe: the Agent runs fixed operations and writes every error message itself, echoing only what the researcher sent (for example a column name). It stops being safe with arbitrary Python, where a script can put data in an exception or in its output. The rule for the arbitrary-Python slice: never forward text the script produced. The orchestrator gets a fixed code and message, and the details stay in the TRE logs.
- Registration re-reads the review record and then saves it as registered. The store has no compare-and-swap, so a decision recorded in the milliseconds between that re-read and save is overwritten and the task stays waiting (nothing is released). A fix needs a conditional write.
- Orchestrator admins are trusted not to change stored results or hashes, until signed results are added.
- Any `dare-control-admin` user can read the results of every study.
- Orphan files. Submission writes the file before it saves the `released` step. If the row save fails, or a concurrent report wins, the file stays in the orchestrator bucket with no row that points to it. The key holds the SHA-256, so it never overwrites another result. No API call can reach an orphan. An orchestrator admin can find and delete orphans in the bucket.

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
6. Numbers use `.` as the decimal point, with no thousands separators and no exponent. Any other format fails with `non_numeric_value`.
7. Only files directly in the dataset folder count. Subfolders are ignored.
8. Each column name must be unique. Names are case-sensitive, so `age` and `Age` are different columns.

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
