namespace Submission.Api.Services
{
    /// <summary>
    /// Orchestrator-side store for federated task result files.
    /// </summary>
    public interface IFederatedResultStore
    {
        string Bucket { get; }

        /// <summary>
        /// Creates the bucket if needed and checks it has no bucket policy. Never throws.
        /// </summary>
        /// <returns>True when the bucket is ready.</returns>
        Task<bool> EnsureReadyAsync();

        /// <summary>Throws on any storage failure.</summary>
        Task PutAsync(string key, byte[] content, CancellationToken ct);

        /// <returns>Null when the object does not exist. Throws when storage is unreachable.</returns>
        Task<byte[]?> GetAsync(string key, CancellationToken ct);
    }
}
