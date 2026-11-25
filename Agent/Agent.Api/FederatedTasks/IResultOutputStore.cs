namespace Agent.Api.FederatedTasks
{
    /// <summary>The code-scan rule file a task was checked with, kept so later reports name the rules of scan time.</summary>
    public sealed record ScanRecord(string RulesVersion, string RulesSha256);

    /// <summary>
    /// Object store access for federated task result files kept in the TRE output bucket.
    /// All methods throw on storage failure.
    /// </summary>
    public interface IResultOutputStore
    {
        /// <summary>Returns the stored result file for the task, or null if none exists.</summary>
        Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct);

        /// <summary>Stores the result file for the task as application/json.</summary>
        Task SaveAsync(string taskId, byte[] content, CancellationToken ct);

        /// <summary>Records on the stored file where it was released to and its hash.</summary>
        Task MarkReleasedAsync(string taskId, string resultKey, string resultSha256, CancellationToken ct);

        /// <summary>Deletes the stored result file and scan record for the task, if there are any.</summary>
        Task DeleteAsync(string taskId, CancellationToken ct);

        /// <summary>Stores the rules the task's code was scanned with.</summary>
        Task SaveScanRecordAsync(string taskId, ScanRecord record, CancellationToken ct);

        /// <summary>Returns the rules the task's code was scanned with, or null if none were recorded.</summary>
        Task<ScanRecord?> GetScanRecordAsync(string taskId, CancellationToken ct);
    }
}
