using FiveSafesTes.Core.Models.FederatedTasks;

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

        private static DateTime? Utc(DateTime? at) => at is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }
}
