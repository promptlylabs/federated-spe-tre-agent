# TES conformance

Runs GA4GH's TES compliance suite ([ga4gh/openapi-test-runner](https://github.com/ga4gh/openapi-test-runner)) against the
dev stack's TES API (`/v1/...`, see "The TES API" in `docs/federated-tasks.md`), through `proxy.py`, which:

- adds the `dare-control-admin` login the suite cannot send;
- with `MODE=federated`, replaces each submitted task's body with a valid federated study (keeping the suite's `name`,
  `description` and its own tags), each with its own `fed.study_id`, as DAAMS would.

`run.sh` runs the suite twice: as published (`stock`) and with federated bodies (`federated`). Each create is a real study
on the dev stack: cancel them afterwards (`run.sh` prints where their IDs are).

## Results, 2026-11-27 (TES 1.1.0, suite commit pinned in `run.sh`)

| Mode | Passed | Failed | Why the failures |
|---|---|---|---|
| `federated` | 145 | 2 | Both by design: `create_task_backend_parameters` expects a 400 for unsupported backend parameters, but the proxy sends a valid federated body instead (the real profile refuses any `backend_parameters` with 400); `filter_task_by_tag` expects the task to carry exactly its 4 tags, and a federated task also carries `fed.sites`, `fed.study_id` and `fed.code_sha256`. |
| `stock` | 47 | 21 | All the same reason: each is the suite submitting a generic task (`alpine`, `echo hello`), which this service refuses with 400 because it accepts federated studies only. |

Every check that does not depend on submitting a generic task passes, `service-info` included. The claim this supports:
**the service implements GA4GH TES 1.1 for submission, status, listing and cancellation, restricted to a federated task
profile.** Not "TES compliant" without that qualifier, and not "TES end to end" (nodes use the TES task format and
states, plus two reporting calls of our own: `docs/node-contract.md`).
