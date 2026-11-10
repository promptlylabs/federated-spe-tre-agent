namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Names of the multipart form parts of a federated task report. Shared by the Agent and Submission.</summary>
    public static class FederatedReportFields
    {
        public const string TaskId = "TaskId";
        public const string State = "State";
        public const string ErrorCode = "ErrorCode";
        public const string ErrorMessage = "ErrorMessage";
        public const string DatasetSha256 = "DatasetSha256";
        public const string RejectedAt = "RejectedAt";
        public const string ResultFile = "result";
    }
}
