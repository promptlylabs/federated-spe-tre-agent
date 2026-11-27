# What this repository takes from 5S-TES, and what it changed

This repository is a modified copy of [Five Safes TES](https://github.com/SwanseaUniversityMedical/5s-Tes) (5S-TES), from Swansea University, under the Apache License 2.0 (`LICENSE`). The federated SPE for SPMS is built on it. This document says which parts of 5S-TES the federated SPE uses, which parts it does not use, what it changed, and why.

Status: 2026-11-27. The marking of modified files that the licence asks for is ANZE-6061.

## In short

The federated SPE uses the same architecture pattern as 5S-TES: a central service holds the work, an Agent inside each TRE polls it, and a person in each TRE approves every output before it leaves. It also uses the GA4GH TES task as its job format, and Keycloak for identities.

The solution itself is different. The unit of work, what runs in the TRE, what comes back, how results are combined, and how disclosure is controlled are all new (see "What changed, and why"). The 5S-TES job pipeline (the `/v1/tasks` routes, RabbitMQ, Funnel, projects and their credentials) is not used by the federated SPE.

## What the federated SPE uses from 5S-TES

All of these parts were changed for the federated SPE.

| Part | Where | Used for |
|---|---|---|
| Service shells | `Submission/Submission.Api`, `Agent/Agent.Api`, `Egress/Data-Egress-API`, `Egress/Data-Egress-UI` | Hosting, Keycloak login, Postgres, Swagger |
| TRE registry | the `Tres` table in Submission | The hospitals that exist, and the Keycloak client of each |
| Audit log | the `AuditLogs` table in Submission | The audit trail of a study |
| GA4GH TES model classes | `Shared/FiveSafesTes.Core` | The task format: a federated task is a TES task |
| Hangfire | the Agent | The poll that does the work at a hospital and at the aggregator |
| Egress app | `Egress/` | The output approval by the hospital's reviewer, with a federated review page |
| Keycloak realms | `dev/federated/keycloak/` | The layout of the realms and roles |

## What the federated SPE does not use

These parts of 5S-TES are in the repository but take no part in the federated SPE. The federated dev stack (`dev/federated/`) runs without them.

| Part | Where |
|---|---|
| The native TES routes: create, get, list and cancel a task | `Submission/Submission.Api/Controllers/TESController.cs` |
| Submissions, with a child submission per TRE, projects and their memberships | the `Submissions` tables and the project code in Submission |
| RabbitMQ messaging between the services | `Shared/FiveSafesTes.Core/Rabbit`, setup in each `Program.cs` |
| The native processing in the Agent, and the Funnel TES executor | `Agent/Agent.Api/Services/SubmissionHelper.cs`, `funnel/` |
| Credentials minted per project, OPA policies, LDAP | `Credentials/`, `OPASettings/`, `ldap-init/` |
| Teleport and Kibana | `Teleport/`, `kibana-provisioning/` |
| The web front ends | `Submission/Submission.Web` (except a small federated addition), `Agent/Agent.Web`, `Agent/agent-web` |

Unused code still has a cost: people read it, build it and review it, and some of it is reachable. For example, the native `GET /v1/tasks` routes accept anonymous callers. Removing the native pipeline is planned work for after the PoC.

## What changed, and why

| | 5S-TES | Federated SPE | Why |
|---|---|---|---|
| Unit of work | A submission per project, with a child submission per TRE | A study, with versions; each version has a part per hospital, with phases, a deadline and one outcome (`docs/federated-tasks.md`, "Studies and versions") | A study runs at several hospitals and must end with one result, or one clear reason why there is none |
| What runs in the TRE | Any container image that the submitter names, run by Funnel | An image from the hospital's `ApprovedImages`, today `python-runner:1`, with a fixed command, no network, a read-only file system and no package installers. The code is scanned before it runs, and the dataset is checked against its `schema.json` (`runner/README.md`) | A hospital can review a short Python file. It cannot review an arbitrary image. A fixed image also makes the researcher's SPE and the hospitals run the same environment |
| What comes back | Output files from each TRE, to the researcher | A small JSON result document from each hospital, with size limits, to the orchestrator only | A small, typed result can be reviewed and checked. Files cannot be checked automatically |
| Combining results | The researcher gets the output of each TRE and combines it | The researcher's `aggregate()` runs centrally in the aggregator. The researcher gets only the pooled result | The result of one hospital never reaches the researcher |
| Disclosure control | The TRE's reviewer reads the outputs | The TRE's reviewer, then small-number checks on the pooled result with ACRO's evidence (`docs/proposals/small-numbers-at-aggregation.md`) | Small numbers are checked where they are released, after pooling |
| Study rules | Each TRE approves or not | At least 2 hospitals must release, a deadline, cancellation that waits for each hospital, an audit trail | One hospital that does not answer cannot hold up a study, and every step can be traced |
| Who can submit | Project membership, and credentials minted per project | The DAAMS data permit, as `fed.study_id`. The `dare-control-admin` role for now (ANZE-6148) | SPMS grants access with data permits in DAAMS |
| Messaging | RabbitMQ between the services, and HTTP | HTTP only. The Agents and the aggregator poll Submission | One channel and one set of secrets fewer |
| Results in storage | Per-project buckets | Results stored by their SHA-256, and checked against it when stored, read back and reviewed | Each step can prove that it has the bytes the previous step approved |

## The federated code

The federated SPE adds its own code next to the 5S-TES code, with "Federated" in the names: `FederatedTaskController`, `FederatedAggregationController` and their services in Submission, `FederatedTasks/` and `DoFederatedTaskWork` and `DoFederatedAggregationWork` in the Agent, `Models/FederatedTasks/` in `Shared/FiveSafesTes.Core`, the federated review in the Egress app, the runner image in `runner/`, and the dev stack in `dev/federated/`. `docs/federated-tasks.md` describes how it works.

## Open points

- **Remove the native pipeline.** After the PoC: delete the parts in "What the federated SPE does not use", and move the federated code into projects of its own. Then decide if the remaining shells are worth keeping or are simpler to write again. That depends on the production design: SPMS's SPE operator may start the run at a hospital, and the output review may move to Lake Admin (Dev 09).
- **Security between the services.** Every call carries a Keycloak token, each hospital has its own client, and the routes check roles. Before production: TLS on every connection (the dev stack uses plain HTTP), signed results (ANZE-6014), a token kept until it expires instead of a new one for each call (ANZE-6023), and possibly mutual TLS for the Agents.
- **More runner images.** A researcher can name an image, but each hospital runs only the images in its `ApprovedImages`. For R or other languages, the plan is more runner images with the same contract (`run-analysis`, `result.json`, locked down), not images of the researcher's choice.
- **Marking modified files** as the Apache License asks: ANZE-6061.
