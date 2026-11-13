namespace Submission.Api.Models
{
    /// <summary>
    /// Text fields of a federated task report (multipart/form-data). The result file is read from
    /// <c>Request.Form.Files</c> so the number and names of file parts can be checked.
    /// </summary>
    public sealed class FederatedTaskReportForm
    {
        // Nullable so a missing field is not a model-state error: the identity check must run before any validation.
        public string? TaskId { get; set; }

        /// <summary>TES state as sent on the wire: COMPLETE, EXECUTOR_ERROR, SYSTEM_ERROR or CANCELED.</summary>
        public string? State { get; set; }

        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public string? DatasetSha256 { get; set; }

        /// <summary>For a CANCELED report: <c>input_checks</c> or <c>output_approval</c>.</summary>
        public string? RejectedAt { get; set; }
    }
}
