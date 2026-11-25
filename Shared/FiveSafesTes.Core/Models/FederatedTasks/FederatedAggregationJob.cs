namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// A study version ready for aggregation, as Submission hands it to the aggregator. <see cref="ResultsJson"/> is a
    /// JSON array of each released hospital's <c>result.value</c>, in <c>fed.sites</c> order, read back from the stored
    /// files after their hashes were checked. It carries no hospital names or task IDs: aggregate() sees values only.
    /// </summary>
    public sealed class FederatedAggregationJob
    {
        public Guid VersionId { get; set; }
        public string StudyId { get; set; } = string.Empty;
        public int Version { get; set; }

        /// <summary>The TES task as submitted. The aggregator takes the code from it and checks its hash.</summary>
        public string TesTaskJson { get; set; } = string.Empty;

        public string CodeSha256 { get; set; } = string.Empty;
        public string ResultsJson { get; set; } = "[]";
    }

    /// <summary>The phases and form fields of an aggregation report (multipart/form-data, file part <c>result</c>).</summary>
    public static class FederatedAggregationPhases
    {
        public const string Running = "running";
        public const string Aggregated = "aggregated";
        public const string Failed = "failed";

        public const string VersionIdField = "VersionId";
        public const string PhaseField = "Phase";
        public const string ErrorCodeField = "ErrorCode";

        /// <summary>The codes an aggregation may fail with; a report with any other is refused.</summary>
        public static readonly IReadOnlySet<string> FailureCodes = new HashSet<string>
        {
            FederatedTaskErrorCodes.InvalidTask, FederatedTaskErrorCodes.ImageNotApproved, FederatedTaskErrorCodes.CodeError,
            FederatedTaskErrorCodes.InvalidResult, FederatedTaskErrorCodes.ResultTooLarge, FederatedTaskErrorCodes.Timeout,
            FederatedTaskErrorCodes.OutOfMemory, FederatedTaskErrorCodes.RunFailed, FederatedTaskErrorCodes.InternalError
        };
    }
}
