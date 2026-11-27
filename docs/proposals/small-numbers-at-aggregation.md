# Proposal: withhold small numbers when hospital results are pooled

| | |
|---|---|
| Status | Draft for discussion with SPMS and the participating hospitals |
| Date | 2026-11-27 |
| Scope | Federated SPE: hospitals, orchestrator and aggregator |
| Jira | ANZE-6142 |

## 1. Summary

A result that is based on very few patients can identify those patients. Today, only the reviewer at each hospital can stop such a result, by eye.

We propose to use ACRO, the output-checking tool that Dev 09 adds to the SPE, in its **federated mode**, and to apply the small-number rules **after** the hospital results are pooled:

1. At each hospital, the researcher's code builds its tables with ACRO. ACRO records the counts and totals behind each table. Nothing is withheld at the hospital.
2. The reviewer at each hospital approves or rejects the result in the Egress app, as today. The reviewer also sees the `aggregate()` function that will combine the result with the results of the other hospitals.
3. The orchestrator adds the ACRO evidence of all hospitals together. Then it applies the small-number rules to the **pooled** tables and withholds each cell that fails.
4. Values that ACRO did not produce go to the researcher only after the DAAMS export review, marked "not checked for small numbers".

A cell of 3 patients at one hospital and 9 at another becomes a pooled cell of 12. The pooled cell is safe to release, so the researcher gets it. A rule at each hospital would withhold the 3, and the pooled figure would be wrong.

This design needs a base of trust between the hospitals and the orchestrator, because the orchestrator sees each hospital's counts before they are pooled. Section 6 gives the conditions of that trust.

## 2. The problem

The researcher's code runs at each hospital and returns a result. The result can contain any numbers that the code calculates. Some of these numbers can disclose a person:

- A count of 1 or 2 patients in a category, for example "2 patients in the south region with diagnosis X".
- A mean of a very small group. The mean age of 2 patients nearly gives their ages.
- A total that one person dominates. If one patient has 90% of the cost, the total discloses that patient's cost.

The usual protection in a Trusted Research Environment (TRE) is statistical disclosure control (SDC). SDC applies fixed rules to each output before it is released, and a trained person reviews the output.

## 3. ACRO

ACRO (Automatic Checking of Research Outputs) is a Python library from the SACRO project, funded by DARE UK. Dev 09 adds ACRO to the SPE image in a fixed version (RF-SAI-02). The hospital runner image uses the same version. Under Dev 09, ACRO is optional for the researcher, and an output without ACRO follows the manual review. SPMS has not set its thresholds yet (Dev 09, PA-11). This proposal uses ACRO 1.1.0 and its default thresholds until SPMS sets them.

The researcher builds tables through ACRO, not directly through pandas. The functions have the same arguments as the pandas functions:

```python
table = acro.crosstab(df.region, df.sex)                                  # counts
means = acro.crosstab(df.region, df.sex, values=df.age, aggfunc="mean")   # a statistic per cell
```

Because ACRO calculates each table, it knows how many people are behind each cell. ACRO's rules, with its default values:

| Rule | Default | Applies to |
|---|---|---|
| Minimum threshold | at least 10 people in each cell | counts, and statistics per cell |
| Zeros | a zero count is disclosive | counts |
| Dominance (n, k) | the 2 largest values must be less than 90% of the cell total | sums and means |
| p% rule | the smaller values must be at least 10% of the largest value | sums and means |

ACRO has two modes:

- **Standalone mode** applies the rules where the data is. It can withhold the failing cells and recalculate the totals. It marks each table "pass", "review" or "fail" for the reviewer.
- **Federated mode** does not apply the rules where the data is. It writes the **evidence** for each table: the counts, sums and largest values per cell. A trusted aggregator then applies the rules to the evidence from all sites together.

We ran ACRO 1.1.0 on test data to confirm this behavior. ACRO 1.1.0 writes the federated evidence, but it does not include the aggregator. The orchestrator applies ACRO's rules itself, with the same thresholds.

## 4. Security of a result, from start to end

### At the hospital

1. **Checks before the code runs.** The hospital scans the code with its own rules. The code runs in an isolated container with no network. (These controls exist today.)
2. **ACRO in federated mode.** The researcher builds tables with ACRO. ACRO records the evidence behind each table. Nothing is withheld at the hospital, because pooling can make a small cell safe.
3. **The hospital reviewer decides in the Egress app.** The reviewer sees:
   - the result, which contains ACRO's evidence;
   - the `aggregate()` function that will combine the result, under its own heading;
   - a statement that the pooled result is checked for small numbers before the researcher gets it.

   The reviewer can reject a result for any reason. Examples: the result contains patient-level data instead of statistics, or `aggregate()` releases one hospital's values unchanged.

   The aggregator runs only the code that the hospital saw. It compares the SHA-256 of the code with the value recorded when the study was submitted, and it runs nothing if they differ.

### Between the hospital and the orchestrator

4. **A base of trust.** The hospitals accept that their approved results can contain small counts, and that these go to the orchestrator, under the conditions in section 6.

### At the orchestrator

5. **Pooling of the ACRO tables.** The orchestrator adds the evidence of the hospitals together and builds each pooled table again. The researcher's code has no part in this step.
6. **Small-number rules on the pooled tables.** The orchestrator withholds each pooled cell that is less than the minimum, and each zero cell. If a table then has exactly one withheld cell, the orchestrator also withholds the smallest released cell, because the released `n` minus the other cells would give away the withheld one. It recalculates the totals without the withheld cells. It puts the labels of each table in a fixed order, so the order does not show which hospital had a category.
7. **A pooled `n` that is less than the minimum.** The pooled `n` is the sum of the hospitals' `n`. If it is less than the minimum, the orchestrator releases no pooled result, and the study version ends with its own outcome code.
8. **A record of what was withheld.** The pooled result lists each withheld cell and the minimum that was used.

### Before the researcher

9. **DAAMS export review.** A person reviews the pooled result before the researcher gets it. The researcher gets only the pooled result, never the result of one hospital.

## 5. Values that ACRO did not produce

A researcher does not have to use ACRO. This agrees with Dev 09: an output without ACRO is not rejected for that reason, and it follows the manual review.

| Values | How they are pooled | Check before the researcher |
|---|---|---|
| ACRO tables | The orchestrator pools them from the evidence | The small-number rules (section 4, steps 6 to 8), then the DAAMS export review |
| Other values | The researcher's `aggregate()` | The DAAMS export review only. The pooled result marks them "not checked for small numbers". |

So the DAAMS reviewer knows which part of the pooled result needs the most attention. Researchers who use ACRO get automatic checks against accidental disclosure. ACRO cannot pool medians, standard deviations, percentiles or models. Those values go through `aggregate()`.

## 6. Trust model

### What each party sees

| Party | Sees | Does not see |
|---|---|---|
| Hospital reviewer | The hospital's own result, its ACRO evidence and the `aggregate()` function, before the result leaves the hospital | Other hospitals' results |
| Orchestrator (aggregator) | Each approved hospital result, including small counts | Patient-level data |
| DAAMS export reviewer | The pooled result, with the withheld cells and the values marked "not checked" | Any single hospital's result, any withheld cell |
| Researcher | The pooled result, after the DAAMS export review | Any single hospital's result, any withheld cell |

### Conditions of the trust

The orchestrator must meet these conditions before production use:

1. **Only the aggregator reads hospital results.** Administrators of the orchestrator cannot read a single hospital's result. (Today an administrator can read it through `GetTaskResult`.)
2. **Hospital results are deleted after pooling.** The orchestrator keeps the pooled result and the audit trail, not the hospital results.
3. **Every read is audited.** The study's audit trail records each time a hospital result is read.
4. **The data-sharing agreement states the trust.** Each hospital's agreement with SPMS says that approved results can contain small counts and that only the aggregator processes them.

### What each hospital still controls

- **The release.** Nothing leaves the hospital without the approval of its own reviewer.
- **The code that runs.** The hospital scans the researcher's code with its own rules before it runs.
- **The method of combination.** The reviewer sees `aggregate()` before approving, and the aggregator runs only that code.

At the hospital, the main risk is not a small count. The main risk is patient-level data inside a result, for example a list of every patient's age. The hospital reviewer is the protection against this risk.

## 7. What is withheld

The orchestrator applies the minimum threshold and the zero rule to each pooled table. The minimum is a setting of the orchestrator. The default is 10, the ACRO default, until SPMS sets its thresholds.

| Case | Result |
|---|---|
| A pooled cell is 10 or more | Released |
| A pooled cell is less than 10 (for example 2 + 1 + 3 = 6), or 0 | Withheld. A mean or sum in that cell is withheld too. The totals are recalculated without it. |
| A table has exactly one withheld cell | The smallest released cell is withheld too, so the withheld cell cannot be calculated from the pooled `n`. |
| The pooled `n` of the study is less than the minimum | No pooled result. The study version ends with its own outcome code. |
| Nothing fails | Released unchanged |

### Example

| Region × sex | Hospital A | Hospital B | Hospital C | Pooled | Released |
|---|---|---|---|---|---|
| east, F | 0 | 11 | 0 | 11 | 11 |
| east, M | 0 | 2 | 0 | 2 | withheld |
| north, F | 14 | 9 | 7 | 30 | 30 |
| north, M | 11 | 12 | 5 | 28 | 28 |
| south, F | 3 | 4 | 5 | 12 | 12 |
| south, M | 2 | 1 | 3 | 6 | withheld |

Each hospital releases its small cells to the orchestrator. The researcher gets "south, F = 12", although that cell is small at every hospital, and no value for "south, M" or "east, M". Two cells are withheld, so the pooled `n` does not give either of them away. The totals count the released cells only.

## 8. Change to the Dev 15 specification

The Dev 15 specification (section 4.7, "Controlo de saídas") has three layers of output control:

1. An automatic gate at each node. A node result with a cell below the threshold does not leave the node, and the researcher sees "resultado suprimido" for that node (RF-OUT-01, test TC-13).
2. An automatic assessment with ACRO at the coordinator, on the combined result (RF-OUT-04).
3. A human decision before any output leaves the coordinator (RF-OUT-05).

This proposal changes layer 1. It keeps layers 2 and 3.

**What changes.** A node does not withhold small cells automatically. The reviewer at the node approves or rejects the result, with the result, ACRO's evidence and the `aggregate()` function in front of them. The small-number rules apply to the pooled result at the coordinator.

**Why.**

- **The researcher never gets a node result.** In this system the researcher gets only the pooled result. (Scenario A of Dev 15 shows the node results to the researcher. This system does not.) So a small cell at one node does not reach the researcher, and a gate at the node gives no additional protection to patients.
- **A gate at the node makes the pooled result wrong.** If a node withholds a cell of 3, the pooled cell loses those 3 patients. If the other nodes have 9, the correct pooled cell is 12 and safe to release, but the researcher gets 9 or nothing.
- **The node keeps its control.** The node reviewer still decides each release, and now sees how the result will be combined.

**What the node gives in return.** The node trusts the coordinator with small counts, under the conditions in section 6.

## 9. What researchers do

- Build each table that must be pooled and checked with ACRO: `acro.crosstab` or `acro.pivot_table`, with counts, sums or means. The runner gives `site_analysis` an ACRO session as its second argument: `def site_analysis(df, acro):`.
- Return other values in the dict, as today, and combine them in `aggregate()`. These values are released only after the DAAMS export review, marked "not checked for small numbers".
- In the SPE, the researcher can use ACRO in standalone mode to see ACRO's verdicts on the synthetic data. Before submission, the dry run gives the same code a federated session, as the hospital does.

## 10. Delivery

### First delivery (ANZE-6142)

1. ACRO 1.1.0 in the hospital runner image, in federated mode, in a read-only folder.
2. The runner gives `site_analysis` an ACRO session, and adds ACRO's evidence (counts and sums per cell) to the hospital result.
3. The hospital result and the job of the aggregator carry the evidence.
4. The aggregator pools the ACRO tables, applies the minimum threshold and the zero rule, withholds the failing cells and recalculates the totals.
5. The pooled result contains the pooled tables, the withheld cells, the minimum, and the values of `aggregate()` marked "not checked for small numbers". A pooled `n` that is less than the minimum gives no pooled result and its own outcome code.
6. The Egress app shows `aggregate()` under its own heading, with the statement about the pooled checks.

### Next deliveries

- The dominance and p% rules on pooled sums and means.
- The trust conditions in section 6: only the aggregator reads hospital results, every read is audited, and hospital results are deleted after pooling.
- An automatic check that `aggregate()` does not release one hospital's values unchanged.
- The number of hospitals in the study, and ACRO's view of each table, in the Egress app.
- The history of the queries of a study and a limit on runs per study, against differencing (Dev 15 RF-OUT-02 and RF-OUT-03).
- The SPE tools: the dry run before submission and the templates use ACRO.

## 11. Limits

The proposal does not protect against all disclosure. These risks stay with the human reviewers:

- **Differencing between study versions.** A researcher can submit two versions that differ by one patient and compare the pooled results. The next deliveries add a query history and a limit on runs.
- **Disclosure from other outputs.** The orchestrator recalculates the totals of a table, but it cannot compare one table with another table.
- **Values outside ACRO.** The code can return the counts of an ACRO table also as ordinary values. These values go through `aggregate()` and are marked "not checked". The DAAMS reviewer must check them.
- **ACRO runs in the researcher's process.** The ACRO check protects against accidental disclosure, not against a researcher who wants to defeat it. The code can change the evidence, or give ACRO padded data (for example, the real rows plus invented ones) so that every pooled cell reaches the minimum, and then remove the padding from the released numbers. The researcher's code also states each hospital's `n`. Only a person who reads the code catches this: the hospital reviewer, who sees the code, and the DAAMS reviewer.
- **One engine.** Dev 15 asks for one risk engine, shared with Dev 09. The rules of this proposal run in the aggregator. Connecting them to the risk module of Lake Admin is not in this proposal.

## 12. Open decisions

For SPMS and the hospitals:

1. **The trust model.** Do the hospitals accept that their approved results can contain small counts that go to the orchestrator, under the conditions in section 6? This replaces the gate at the node in Dev 15 RF-OUT-01 (section 8).
2. **The thresholds.** SPMS has not set its thresholds yet (Dev 09, PA-11, and Dev 15 open point 13). Until SPMS sets them, the aggregator uses ACRO's defaults (minimum 10 people in each cell).
3. **Retention.** When does the orchestrator delete hospital results: immediately after pooling, or after a fixed period for audit?
4. **The aggregator of ACRO.** We will ask the SACRO team if they have code that pools the federated evidence of several sites. If they do, a later delivery can use it.
