using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;

namespace Submission.Api.Services
{
    /// <summary>The wire view of a study version, and cancelling one.</summary>
    public static class FederatedStudyVersions
    {
        /// <summary>
        /// The version with one entry per part, in <see cref="FederatedStudyVersion.Sites"/> order. The parts and their
        /// phase changes must be loaded. No result content is ever included, only the result's hash.
        /// </summary>
        public static FederatedStudyVersionView ToView(FederatedStudyVersion version)
        {
            var sites = version.Sites.Split('|').ToList();
            return new FederatedStudyVersionView
            {
                StudyId = version.StudyId,
                Version = version.Version,
                VersionId = version.VersionId,
                SubmittedAt = DateTime.SpecifyKind(version.SubmittedAt, DateTimeKind.Utc),
                CodeSha256 = version.CodeSha256,
                State = version.State.ToWire(),
                Outcome = version.Outcome?.ToWire(),
                OutcomeReason = version.OutcomeReason,
                OutcomeMessage = version.OutcomeMessage,
                OutcomeAt = Utc(version.OutcomeAt),
                DeadlineAt = DateTime.SpecifyKind(version.DeadlineAt, DateTimeKind.Utc),
                CanceledAt = version.Outcome == FederatedStudyOutcome.Canceled ? Utc(version.OutcomeAt) : null,
                ReadyAt = Utc(version.ReadyAt),
                AggregationStartedAt = Utc(version.AggregationStartedAt),
                AggregationErrorCode = version.AggregationErrorCode,
                PooledResultSha256 = version.PooledResultSha256,
                Hospitals = version.Parts
                    .OrderBy(p => sites.IndexOf(p.TreName))
                    .Select(p => new FederatedHospitalView
                    {
                        Tre = p.TreName,
                        TaskId = p.TaskId,
                        Phase = FederatedTaskPhases.ToWire(p.Phase),
                        State = p.State,
                        Times = p.PhaseTimes(),
                        ErrorCode = p.Phase == FederatedTaskPhase.Failed || FederatedTaskPhases.IsRejection(p.Phase) ? p.ErrorCode : null,
                        ResultSha256 = p.Phase == FederatedTaskPhase.Released ? p.ResultSha256 : null,
                        ScanRulesVersion = p.ScanRulesVersion,
                        ScanRulesSha256 = p.ScanRulesSha256
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// Failures of the researcher's code: EXECUTOR_ERROR. Any other aggregation failure (the task, the image, the run,
        /// the platform) is SYSTEM_ERROR.
        /// </summary>
        private static readonly HashSet<string> ResearcherCodeFailures =
        [
            FederatedTaskErrorCodes.CodeError, FederatedTaskErrorCodes.InvalidResult, FederatedTaskErrorCodes.ResultTooLarge,
            FederatedTaskErrorCodes.Timeout, FederatedTaskErrorCodes.OutOfMemory, FederatedTaskErrorCodes.UnsafeCode,
            FederatedTaskErrorCodes.SmallNumbers
        ];

        /// <summary>
        /// The version as one GA4GH TES task, for TES clients such as DAAMS: the task as submitted, a standard TES state
        /// (docs/federated-tasks.md, "Reading a study") and, in metadata, the outcome and each hospital's task id and phase in
        /// fed.sites order. The parts must be loaded. Never any result content: only the pooled result's hash.
        /// </summary>
        public static TesTask ToTesTask(FederatedStudyVersion version)
        {
            var task = (string.IsNullOrEmpty(version.TesTaskJson) ? null : JsonConvert.DeserializeObject<TesTask>(version.TesTaskJson)) ?? new TesTask();
            task.Id = version.VersionId.ToString();
            task.State = TesStateOf(version);
            task.CreationTime = DateTime.SpecifyKind(version.SubmittedAt, DateTimeKind.Utc);

            var metadata = new Dictionary<string, string>
            {
                ["study_id"] = version.StudyId,
                ["version"] = version.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["version_state"] = version.State.ToWire(),
                ["deadline_at"] = DateTime.SpecifyKind(version.DeadlineAt, DateTimeKind.Utc).ToString("O")
            };
            Add(metadata, "outcome", version.Outcome?.ToWire());
            Add(metadata, "outcome_reason", version.OutcomeReason);
            Add(metadata, "outcome_message", version.OutcomeMessage);
            Add(metadata, "outcome_at", Utc(version.OutcomeAt)?.ToString("O"));
            Add(metadata, "error_code", version.Outcome == FederatedStudyOutcome.Failed ? version.AggregationErrorCode : null);
            Add(metadata, "pooled_result_sha256", version.PooledResultSha256);
            var sites = version.Sites.Split('|').ToList();
            foreach (var part in version.Parts.OrderBy(p => sites.IndexOf(p.TreName)))
            {
                metadata[$"hospital.{part.TreName}.task_id"] = part.TaskId.ToString();
                metadata[$"hospital.{part.TreName}.phase"] = FederatedTaskPhases.ToWire(part.Phase);
            }

            task.Logs = [new TesTaskLog { Metadata = metadata }];
            return task;
        }

        private static TesState TesStateOf(FederatedStudyVersion version) => version.State switch
        {
            FederatedStudyVersionState.Open => TesState.RUNNINGEnum,
            FederatedStudyVersionState.Canceling => TesState.CANCELINGEnum,
            _ => version.Outcome switch
            {
                FederatedStudyOutcome.ReadyForAggregation => TesState.RUNNINGEnum,
                FederatedStudyOutcome.Aggregated => TesState.COMPLETEEnum,
                FederatedStudyOutcome.Failed when version.AggregationErrorCode is { } code && ResearcherCodeFailures.Contains(code) => TesState.EXECUTORERROREnum,
                FederatedStudyOutcome.Failed => TesState.SYSTEMERROREnum,
                _ => TesState.CANCELEDEnum
            }
        };

        private static void Add(Dictionary<string, string> metadata, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) metadata[key] = value;
        }

        /// <summary>
        /// Gives the version the canceled outcome and asks every unfinished part to cancel: a queued part is cancelled at
        /// once, a running or waiting one waits in canceling until its hospital confirms. Nothing is saved here.
        /// </summary>
        public static void RequestCancel(FederatedStudyVersion version, DateTime atUtc)
        {
            version.Outcome = FederatedStudyOutcome.Canceled;
            version.OutcomeReason = FederatedStudyOutcomeReasons.Canceled;
            version.OutcomeMessage = "cancelled by an orchestrator admin";
            version.OutcomeAt = atUtc;
            foreach (var part in version.Parts)
            {
                part.RequestCancel(atUtc);
            }
        }

        /// <summary>The audit trail of a version. The parts and their phase changes must be loaded.</summary>
        public static FederatedStudyAuditView ToAudit(FederatedStudyVersion version)
        {
            var sites = version.Sites.Split('|').ToList();
            return new FederatedStudyAuditView
            {
                StudyId = version.StudyId,
                Version = version.Version,
                VersionId = version.VersionId,
                SubmittedBy = version.SubmittedBy,
                SubmittedAt = DateTime.SpecifyKind(version.SubmittedAt, DateTimeKind.Utc),
                CodeSha256 = version.CodeSha256,
                DeadlineAt = DateTime.SpecifyKind(version.DeadlineAt, DateTimeKind.Utc),
                Outcome = version.Outcome?.ToWire(),
                OutcomeReason = version.OutcomeReason,
                OutcomeMessage = version.OutcomeMessage,
                OutcomeAt = Utc(version.OutcomeAt),
                ReadyAt = Utc(version.ReadyAt),
                AggregationStartedAt = Utc(version.AggregationStartedAt),
                AggregationErrorCode = version.AggregationErrorCode,
                PooledResultSha256 = version.PooledResultSha256,
                Hospitals = version.Parts
                    .OrderBy(p => sites.IndexOf(p.TreName))
                    .Select(p => new FederatedHospitalAudit
                    {
                        Tre = p.TreName,
                        TaskId = p.TaskId,
                        Phases = p.PhaseChanges.OrderBy(c => c.At).ThenBy(c => c.Id)
                            .Select(c => new FederatedPhaseAudit
                            {
                                Phase = FederatedTaskPhases.ToWire(c.Phase),
                                At = DateTime.SpecifyKind(c.At, DateTimeKind.Utc),
                                ErrorCode = c.ErrorCode
                            })
                            .ToList(),
                        ResultSha256 = p.Phase == FederatedTaskPhase.Released ? p.ResultSha256 : null
                    })
                    .ToList()
            };
        }

        private static DateTime? Utc(DateTime? at) => at is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }
}
