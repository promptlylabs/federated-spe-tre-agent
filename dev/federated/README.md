# Local federated task stack

A Docker Compose stack for trying the federated task flow on your machine:
queue a task in Submission.Api → the Agent polls it → reads the CSV from the TRE's RustFS → saves the result as a JSON file in the TRE (`federated-outputs`) → uploads it to the orchestrator's RustFS (`federated-results`).
See [docs/federated-tasks.md](../../docs/federated-tasks.md) for the flow itself.

For local development only. All passwords below are throwaway dev values.

## Start and stop

From the repo root:

```bash
docker compose -f dev/federated/docker-compose.yml up -d --build   # first run builds both APIs (a few minutes)
docker compose -f dev/federated/docker-compose.yml down            # stop (keeps data)
docker compose -f dev/federated/docker-compose.yml down -v         # stop and delete all data
```

On start, two one-shot steps prepare the test data:
- `seed-tre` registers the TRE `my-tre` in Submission;
- `seed-dataset` uploads `data/cohort.csv` as `dataset/cohort_2024/cohort.csv`.

## What runs

| Service | URL on your machine | Login |
|---|---|---|
| Submission.Api (orchestrator) | http://localhost:5034 | Keycloak token, see below |
| Agent.Api (TRE Agent) | http://localhost:5269/hangfire (jobs dashboard) | `admin` / `password` |
| RustFS console (TRE storage) | http://localhost:9001/rustfs/console/ | access key `rustfsadmin` / `rustfsadmin123` |
| RustFS console (orchestrator storage) | http://localhost:9101/rustfs/console/ | access key `orchadmin` / `orchadmin123` |
| Keycloak | http://localhost:8180/admin | `admin` / `admin` |
| Postgres | localhost:55432 | `postgres` / `postgres` |

Keycloak users:

| Realm | User | Password | Role or group |
|---|---|---|---|
| `Dare-Control` | `control-admin` | `ControlAdmin1!` | `dare-control-admin`: can queue tasks |
| `Dare-Control` | `tre-agent-my-tre` | `TreAgent1!` | `dare-tre-admin`: the Agent's own login |
| `Dare-Control` | client `tre-my-tre` | secret `dev-tre-my-tre-secret` | service account with `dare-tre-admin`: what the Agent uses to call Submission |
| `tre-test` | `uploader` | `uploader123` | group `dataset-upload`: RustFS console upload |

The `uploader` login through the RustFS console's "TRE Keycloak" button loops. That is a known RustFS console bug (rustfs/console#271), fixed upstream but not yet released. Use the access-key login for now.

## Try it

```bash
cd dev/federated
./task.sh run cohort_2024 mean --column age --filter sex:eq:F   # COMPLETE {"result_bucket": "federated-results", "result_key": "<tre>/<task>/<sha256>.json", ...}
./task.sh run cohort_2024 count                                 # COMPLETE {"result_key": ...}
./task.sh result <task-id>                                      # downloads and prints the result file
./task.sh run nope count                                        # EXECUTOR_ERROR {"error_code": "dataset_not_found", ...}
./task.sh enqueue cohort_2024 median                            # rejected: unknown_operation
```

`run` queues the task and waits until the Agent has reported. The Agent polls once a minute. On the Hangfire dashboard, **Recurring jobs → Run Federated Tasks → Trigger now** runs it at once.

To test your own data, upload a CSV in the RustFS console to `federated-datasets/dataset/<name>/`, with exactly one `.csv` per folder. Then run `./task.sh run <name> count`.

Where the result files are:
- **Orchestrator:** bucket `federated-results` on the second RustFS (console http://localhost:9101/rustfs/console/, `orchadmin` / `orchadmin123`), key `<treId>/<taskId>/<sha256>.json`. `task.sh result` reads it through Submission.
- **TRE:** bucket `federated-outputs` on the TRE RustFS (console http://localhost:9001/rustfs/console/, `rustfsadmin` / `rustfsadmin123`), key `<taskId>.json`.

Logs: `docker compose -f dev/federated/docker-compose.yml logs -f agent-api`

## Not included

RabbitMQ, Vault, Zeebe, LDAP and the Agent's other jobs (sync, scan, health check, bucket cleanup) are left out. The federated flow does not need them. Agent.Api logs connection errors for RabbitMQ and Vault at startup; these are expected.
