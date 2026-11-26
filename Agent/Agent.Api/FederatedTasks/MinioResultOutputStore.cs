using System.Text.Json;
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
        private static string ScanKeyFor(string taskId) => $"{taskId}.scan.json";

        // A missing bucket (storage down at startup) is created on first use.
        public Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct) =>
            MissingBucketRecovery.ReadAsync(
                () => _helper.GetObjectBytesAsync(_settings.OutputBucket, KeyFor(taskId), ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);

        public Task SaveAsync(string taskId, byte[] content, CancellationToken ct) =>
            MissingBucketRecovery.RunAsync(
                () => _helper.PutObjectAsync(_settings.OutputBucket, KeyFor(taskId), content, "application/json", null, ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);

        public async Task DeleteAsync(string taskId, CancellationToken ct)
        {
            await MissingBucketRecovery.DeleteAsync(() => _helper.DeleteObject(_settings.OutputBucket, KeyFor(taskId)));
            await MissingBucketRecovery.DeleteAsync(() => _helper.DeleteObject(_settings.OutputBucket, ScanKeyFor(taskId)));
        }

        public Task SaveScanRecordAsync(string taskId, ScanRecord record, CancellationToken ct) =>
            MissingBucketRecovery.RunAsync(
                () => _helper.PutObjectAsync(_settings.OutputBucket, ScanKeyFor(taskId), JsonSerializer.SerializeToUtf8Bytes(record),
                    "application/json", null, ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);

        public async Task<ScanRecord?> GetScanRecordAsync(string taskId, CancellationToken ct)
        {
            var bytes = await MissingBucketRecovery.ReadAsync(
                () => _helper.GetObjectBytesAsync(_settings.OutputBucket, ScanKeyFor(taskId), ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);
            return bytes == null ? null : JsonSerializer.Deserialize<ScanRecord>(bytes);
        }

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
