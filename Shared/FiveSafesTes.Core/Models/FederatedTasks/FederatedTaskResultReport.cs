using FiveSafesTes.Core.Models.Tes;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// The outcome a TRE Agent reports back to the orchestrator for a federated task.
    /// </summary>
    public class FederatedTaskResultReport
    {
        public string TaskId { get; set; } = string.Empty;
        public TesState State { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>Lowercase hex SHA-256 of the dataset file that was read; null when it was never read.</summary>
        public string? DatasetSha256 { get; set; }

        /// <summary>For a CANCELED report: where the task was rejected (input_checks or output_approval).</summary>
        public string? RejectedAt { get; set; }
    }
}
