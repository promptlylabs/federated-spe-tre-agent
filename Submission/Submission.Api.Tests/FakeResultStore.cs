using Submission.Api.Services;

namespace Submission.Api.Tests
{
    /// <summary>In-memory <see cref="IFederatedResultStore"/> for tests.</summary>
    public sealed class FakeResultStore : IFederatedResultStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public bool FailPuts { get; set; }
        public bool FailGets { get; set; }

        public string Bucket => "federated-results";

        public Task<bool> EnsureReadyAsync() => Task.FromResult(true);

        public Task PutAsync(string key, byte[] content, CancellationToken ct)
        {
            if (FailPuts)
            {
                throw new IOException("storage down");
            }

            Objects[key] = content;
            return Task.CompletedTask;
        }

        public string? LastGetBucket { get; private set; }

        public Task<byte[]?> GetAsync(string bucket, string key, CancellationToken ct)
        {
            LastGetBucket = bucket;
            if (FailGets)
            {
                throw new IOException("storage down");
            }

            return Task.FromResult(Objects.TryGetValue(key, out var bytes) ? bytes : null);
        }
    }
}
