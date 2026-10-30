namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Object store access for dataset files.
    /// </summary>
    public interface IDatasetStore
    {
        Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct);

        /// <summary>Opens a dataset file. The stream must be seekable: the reader hashes it, then rewinds to parse it.</summary>
        Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    }
}
