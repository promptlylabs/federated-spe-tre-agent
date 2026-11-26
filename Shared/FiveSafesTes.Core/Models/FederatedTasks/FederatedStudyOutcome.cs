namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>How a study version finished. Plain JSON on the study view, not a TES state.</summary>
    public enum FederatedStudyOutcome
    {
        /// <summary>At least 2 hospitals released a result; 3.3 pools them.</summary>
        ReadyForAggregation,

        /// <summary>Fewer than 2 hospitals released, or could still release, a result. Nothing is released to researchers.</summary>
        Incomplete,

        /// <summary>An orchestrator admin cancelled the version.</summary>
        Canceled
    }

    public static class FederatedStudyOutcomeReasons
    {
        /// <summary>A study is pooled only from at least this many hospitals, so no single site's result is ever released.</summary>
        public const int MinimumHospitals = 2;

        public const string AllFinal = "all_final";
        public const string Timeout = "timeout";
        public const string MinimumUnreachable = "minimum_unreachable";
        public const string TimeoutBelowMinimum = "timeout_below_minimum";
        public const string Canceled = "canceled";

        public static string ToWire(this FederatedStudyOutcome outcome) => outcome switch
        {
            FederatedStudyOutcome.ReadyForAggregation => "ready_for_aggregation",
            FederatedStudyOutcome.Incomplete => "incomplete",
            FederatedStudyOutcome.Canceled => "canceled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }
}
