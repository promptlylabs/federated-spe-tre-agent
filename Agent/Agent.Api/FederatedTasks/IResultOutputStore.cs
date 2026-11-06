namespace Agent.Api.FederatedTasks
{
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
    }
}
