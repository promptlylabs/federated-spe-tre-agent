using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Outcome of one step of a task: a result document, an error code and message, or a run that is still in
    /// progress. An error carries the SHA-256 of the dataset when it was read.
    /// </summary>
    /// <remarks>
    /// <see cref="IsRunFailure"/> marks an error that came from a run of the researcher's code. It is never an
    /// input-check rejection, even when its code is also an input-check code.
    /// </remarks>
    public sealed record ExecutionResult(
        FederatedAnalysisResultDocument? AnalysisDocument,
        string? ErrorCode,
        string? ErrorMessage,
        string? DatasetSha256,
        bool IsInProgress = false,
        bool IsRunFailure = false)
    {
        public bool IsSuccess => AnalysisDocument != null;

        public static ExecutionResult Ok(FederatedAnalysisResultDocument document) => new(document, null, null, null);

        public static ExecutionResult Fail(string code, string? message, string? datasetSha256 = null) =>
            new(null, code, message, datasetSha256);

        /// <summary>The run has not finished; the task keeps its phase and is checked again at the next poll.</summary>
        public static ExecutionResult InProgress() => new(null, null, null, null, IsInProgress: true);

        /// <summary>The run of the researcher's code failed: always an executor error.</summary>
        public static ExecutionResult RunFailed(string code, string message, string? datasetSha256 = null) =>
            new(null, code, message, datasetSha256, IsRunFailure: true);
    }
}
