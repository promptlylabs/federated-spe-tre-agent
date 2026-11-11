# Egress

The Data Egress app lets a reviewer approve or reject files that a researcher's
work wants to take out of a Trusted Research Environment. It is made of a Data
Egress API (`Data-Egress-API`), a Data Egress UI (`Data-Egress-UI`) and a shared
library (`BL`).

This code is forked from Swansea University's
[DARE-Control](https://github.com/SwanseaUniversityMedical/DARE-Control), which
is licensed under Apache 2.0 (see `NOTICE` and the repository `LICENSE`). It was
copied from `src/BL`, `src/Data-Egress-API` and `src/Data-Egress-UI` at upstream
commit `573be5fc6295bd91417e69b8ddaaeb594a259be2`. This repository adds a review
step for federated task results, so that the hospital approves each result
before it leaves.

Changes made when forking:

- All three projects target `net10.0` (upstream: `net8.0`).
- The Dockerfiles use `sdk:10.0` / `aspnet:10.0` and build from the repository
  root (`docker build -f Egress/Data-Egress-API/Dockerfile .`).
- The projects are in `5S-TES.sln` under an `Egress` solution folder, and
  `.github/workflows/5S-TES-containers.yaml` builds both images.
