# Egress

The Data Egress app lets a reviewer approve or reject files that a researcher's
work wants to take out of a Trusted Research Environment. It is made of a Data
Egress API (`Data-Egress-API`) and a Data Egress UI (`Data-Egress-UI`). Both use
the repository's shared library, `Shared/FiveSafesTes.Core`.

This code is forked from Swansea University's
[DARE-Control](https://github.com/SwanseaUniversityMedical/DARE-Control), which
is licensed under Apache 2.0 (see `NOTICE` and the repository `LICENSE`). It was
copied from `src/BL`, `src/Data-Egress-API` and `src/Data-Egress-UI` at upstream
commit `573be5fc6295bd91417e69b8ddaaeb594a259be2`. This repository adds a review
step for federated task results, so that the hospital approves each result
before it leaves.

Changes made when forking:

- Upstream's `BL` library is not copied. `BL` is an older copy of what this
  repository calls `Shared/FiveSafesTes.Core`, so the Egress projects reference
  that instead (`BL.*` namespaces became `FiveSafesTes.Core.*`). The Agent and
  the Egress app now share one definition of `EgressSubmission` and `EgressFile`.
  Differences that come from the newer shared code:
  - `EncDecHelper` uses a random IV for each encryption and stores it before the
    ciphertext. Credentials encrypted by upstream's fixed-IV `BL` cannot be read;
    store them again.
  - The UI keeps its own `CustomCookieEvent` (`Data-Egress-UI/Helpers`), copied
    from `BL`, because the shared one refreshes tokens with the Submission app's
    Keycloak settings.
- The projects target `net10.0` (upstream: `net8.0`).
- The Dockerfiles use `sdk:10.0` / `aspnet:10.0`, copy `Shared/FiveSafesTes.Core`
  and build from the repository root
  (`docker build -f Egress/Data-Egress-API/Dockerfile .`).
- The projects are in `5S-TES.sln` under an `Egress` solution folder. CI does
  not build the images yet; the federated dev stack builds them locally.
