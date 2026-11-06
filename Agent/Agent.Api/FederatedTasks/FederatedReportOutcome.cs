namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// What Submission answered to a report: the HTTP status and, when the body parsed, its codes and result location.
    /// The codes are null when the body was not the expected JSON (for example a proxy's HTML error page).
    /// </summary>
    public sealed record FederatedReportOutcome(
        int StatusCode,
        string? ErrorCode,
        string? ErrorMessage,
        string? ResultKey,
        string? ResultSha256)
    {
        public bool Accepted => StatusCode == 200;
    }
}
