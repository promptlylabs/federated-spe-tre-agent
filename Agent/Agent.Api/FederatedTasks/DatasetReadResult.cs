namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Outcome of reading a dataset: a table and the SHA-256 of the file it came from, or a federated task error code and message.
    /// </summary>
    public sealed record DatasetReadResult(DatasetTable? Table, string? ErrorCode, string? ErrorMessage, string? Sha256 = null)
    {
        public bool IsSuccess => Table != null;

        public static DatasetReadResult Ok(DatasetTable table) => new(table, null, null);

        public static DatasetReadResult Fail(string code, string message) => new(null, code, message);
    }
}
