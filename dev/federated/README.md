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
| `Dare-Control` | `tre-agent-my-tre` | `TreAgent1!` | `dare-tre-admin`: a TRE human admin. Only for the negative test that it cannot call the Agent endpoints. The Agent uses the client `tre-my-tre`. |
| `Dare-Control` | client `tre-my-tre` | secret `dev-tre-my-tre-secret` | service account with `dare-tre-admin`: what the Agent uses to call Submission |
| `tre-test` | `uploader` | `uploader123` | group `dataset-upload`: RustFS console upload |

The `uploader` login through the RustFS console's "TRE Keycloak" button loops. That is a known RustFS console bug (rustfs/console#271), fixed upstream but not yet released. Use the access-key login for now.

## Try it

```bash
cd dev/federated
./task.sh run cohort_2024 mean --column age --filter sex:eq:F   # COMPLETE {"result_bucket": "federated-results", "result_key": "<treId>/<taskId>/<sha256>.json", ...}
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
