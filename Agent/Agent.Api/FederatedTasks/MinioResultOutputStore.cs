using Agent.Api.Models;
using FiveSafesTes.Core.Services;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Keeps federated task result files in the TRE output bucket, one object per task.
    /// </summary>
    public class MinioResultOutputStore : IResultOutputStore
    {
        private readonly IMinioTreHelper _helper;
        private readonly FederatedTaskSettings _settings;

        public MinioResultOutputStore(IMinioTreHelper helper, FederatedTaskSettings settings)
        {
            _helper = helper;
            _settings = settings;
        }

        private static string KeyFor(string taskId) => $"{taskId}.json";

        public Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct) =>
            _helper.GetObjectBytesAsync(_settings.OutputBucket, KeyFor(taskId), ct);

        public Task SaveAsync(string taskId, byte[] content, CancellationToken ct) =>
            _helper.PutObjectAsync(_settings.OutputBucket, KeyFor(taskId), content, "application/json", null, ct);

        public Task MarkReleasedAsync(string taskId, string resultKey, string resultSha256, CancellationToken ct) =>
            _helper.SetObjectMetadataAsync(
                _settings.OutputBucket,
                KeyFor(taskId),
                new Dictionary<string, string>
                {
                    ["released-key"] = resultKey,
                    ["released-sha256"] = resultSha256
                },
                ct);
    }
}
