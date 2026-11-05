namespace Submission.Api.Models
{
    /// <summary>
    /// Text fields of a federated task report (multipart/form-data). The result file is read from
    /// <c>Request.Form.Files</c> so the number and names of file parts can be checked.
    /// </summary>
    public sealed class FederatedTaskReportForm
    {
        public string TaskId { get; set; } = string.Empty;

        /// <summary>TES state as sent on the wire: COMPLETE, EXECUTOR_ERROR or SYSTEM_ERROR.</summary>
        public string State { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? DatasetSha256 { get; set; }
    }
}
