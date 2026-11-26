namespace Submission.Api.Models
{
    /// <summary>Text fields of an aggregation report (multipart/form-data); the pooled result is the file part <c>result</c>.</summary>
    public sealed class FederatedAggregationReportForm
    {
        public string? VersionId { get; set; }

        /// <summary>running, aggregated or failed.</summary>
        public string? Phase { get; set; }

        /// <summary>For failed: one of <see cref="FiveSafesTes.Core.Models.FederatedTasks.FederatedAggregationPhases.FailureCodes"/>.</summary>
        public string? ErrorCode { get; set; }

        /// <summary>For failed with code_error: the line of the researcher's file that failed (1 or more).</summary>
        public int? ErrorLine { get; set; }
    }
}
