namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Decides when a study version is finished (ANZE-6130).</summary>
    public static class FederatedStudyOutcomes
    {
        /// <summary>
        /// Sets the version's outcome when it can be decided, and asks every unfinished part to cancel. Only released parts
        /// count. The version ends incomplete as soon as fewer than 2 parts can still release, is ready for aggregation
        /// when every part is final with 2 or more released, and at its deadline is ready with 2 or more released, otherwise
        /// incomplete. The parts must be loaded. Nothing is saved here.
        /// </summary>
        /// <returns>True when an outcome was set; false when the version already had one or must keep waiting.</returns>
        public static bool Decide(FederatedStudyVersion version, DateTime nowUtc)
        {
            if (version.Outcome is not null)
            {
                return false;
            }

            var released = version.Parts.Count(p => p.Phase == FederatedTaskPhase.Released);
            var unfinished = version.Parts.Count(p => !FederatedTaskPhases.IsFinal(p.Phase));
            const int minimum = FederatedStudyOutcomeReasons.MinimumHospitals;

            (FederatedStudyOutcome Outcome, string Reason, string Message) decision;
            if (released + unfinished < minimum)
            {
                decision = (FederatedStudyOutcome.Incomplete, FederatedStudyOutcomeReasons.MinimumUnreachable,
                    $"only {released + unfinished} hospital(s) released or can still release a result; a study needs at least {minimum}");
            }
            else if (unfinished == 0)
            {
                decision = (FederatedStudyOutcome.ReadyForAggregation, FederatedStudyOutcomeReasons.AllFinal,
                    $"{released} hospitals released a result");
            }
            // Compared on ticks, so the DateTime kind never shifts it: both hold UTC.
            else if (nowUtc.Ticks >= version.DeadlineAt.Ticks)
            {
                decision = released >= minimum
                    ? (FederatedStudyOutcome.ReadyForAggregation, FederatedStudyOutcomeReasons.Timeout,
                        $"the deadline passed with {released} released results; the hospitals still working were cancelled")
                    : (FederatedStudyOutcome.Incomplete, FederatedStudyOutcomeReasons.TimeoutBelowMinimum,
                        $"the deadline passed with {released} released result(s); a study needs at least {minimum}");
            }
            else
            {
                return false;
            }

            version.Outcome = decision.Outcome;
            version.OutcomeReason = decision.Reason;
            version.OutcomeMessage = decision.Message;
            version.OutcomeAt = nowUtc;
            if (decision.Outcome == FederatedStudyOutcome.ReadyForAggregation)
            {
                version.ReadyAt = nowUtc;
            }

            foreach (var part in version.Parts.Where(p => !FederatedTaskPhases.IsFinal(p.Phase)))
            {
                part.RequestCancel(nowUtc);
            }

            return true;
        }

        /// <summary>
        /// Fails a version that has waited <paramref name="timeoutMinutes"/> since it became ready for aggregation, so an
        /// aggregator that is down or misconfigured can't leave it waiting for ever. An admin can retry it.
        /// </summary>
        /// <returns>True when the version was failed.</returns>
        public static bool TimeOutAggregation(FederatedStudyVersion version, DateTime nowUtc, int timeoutMinutes)
        {
            if (version.Outcome != FederatedStudyOutcome.ReadyForAggregation || version.ReadyAt is not { } readyAt
                || nowUtc.Ticks < readyAt.AddMinutes(timeoutMinutes).Ticks)
            {
                return false;
            }

            Fail(version, nowUtc, FederatedTaskErrorCodes.AggregationTimeout,
                $"the orchestrator did not pool the results within {timeoutMinutes} minutes; an admin can retry");
            return true;
        }

        /// <summary>Ends aggregation as failed with <paramref name="code"/>. No pooled result. Nothing is saved here.</summary>
        public static void Fail(FederatedStudyVersion version, DateTime nowUtc, string code, string message)
        {
            version.Outcome = FederatedStudyOutcome.Failed;
            version.AggregationErrorCode = code;
            version.OutcomeMessage = message;
            version.OutcomeAt = nowUtc;
        }

        /// <summary>Records the stored pooled result. Nothing is saved here.</summary>
        public static void Aggregate(FederatedStudyVersion version, DateTime nowUtc, string bucket, string key, string sha256)
        {
            version.Outcome = FederatedStudyOutcome.Aggregated;
            version.PooledResultBucket = bucket;
            version.PooledResultKey = key;
            version.PooledResultSha256 = sha256;
            version.OutcomeMessage = "the hospitals' results were pooled";
            version.OutcomeAt = nowUtc;
        }
    }
}
