namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Outcome of finding a dataset by name: the key of its single data file (and of its schema.json when one is there), or a federated task error code and message.
    /// </summary>
    public sealed record DatasetResolveResult(string? FileKey, string? ErrorCode, string? ErrorMessage, string? SchemaKey = null)
    {
        public bool IsSuccess => FileKey != null;

        public static DatasetResolveResult Ok(string fileKey, string? schemaKey = null) => new(fileKey, null, null, schemaKey);

        public static DatasetResolveResult Fail(string code, string message) => new(null, code, message);
    }
}
