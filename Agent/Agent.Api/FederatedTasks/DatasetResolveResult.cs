namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Outcome of finding a dataset by name: the key of its single data file, or a federated task error code and message.
    /// </summary>
    public sealed record DatasetResolveResult(string? FileKey, string? ErrorCode, string? ErrorMessage)
    {
        public bool IsSuccess => FileKey != null;

        public static DatasetResolveResult Ok(string fileKey) => new(fileKey, null, null);

        public static DatasetResolveResult Fail(string code, string message) => new(null, code, message);
    }
}
