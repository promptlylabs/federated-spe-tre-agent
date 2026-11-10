using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Outcome of running a task: a result document, or an error code and message. An error carries the SHA-256 of
    /// the dataset when it was read.
    /// </summary>
    public sealed record ExecutionResult(FederatedResultDocument? Document, string? ErrorCode, string? ErrorMessage, string? DatasetSha256)
    {
        public bool IsSuccess => Document != null;

        public static ExecutionResult Ok(FederatedResultDocument document) => new(document, null, null, null);

        public static ExecutionResult Fail(string code, string? message, string? datasetSha256 = null) =>
            new(null, code, message, datasetSha256);
    }
}
