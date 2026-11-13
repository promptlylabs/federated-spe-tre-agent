using Agent.Api.Models;
using FiveSafesTes.Core.Services;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Keeps review records in the TRE output bucket as "&lt;taskId&gt;.review.json", and the registration marker
    /// as an empty "&lt;taskId&gt;.registered" object.
    /// </summary>
    public class MinioOutputReviewStore : IOutputReviewStore
    {
        private readonly IMinioTreHelper _helper;
        private readonly FederatedTaskSettings _settings;

        public MinioOutputReviewStore(IMinioTreHelper helper, FederatedTaskSettings settings)
        {
            _helper = helper;
            _settings = settings;
        }

        private static string KeyFor(string taskId) => $"{taskId}.review.json";
        private static string MarkerKeyFor(string taskId) => $"{taskId}.registered";
        private static string WithdrawnKeyFor(string taskId) => $"{taskId}.withdrawn";

        public async Task<OutputReview?> GetAsync(string taskId, CancellationToken ct)
        {
            var bytes = await MissingBucketRecovery.ReadAsync(
                () => _helper.GetObjectBytesAsync(_settings.OutputBucket, KeyFor(taskId), ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);
            return bytes is null ? null : OutputReviewJson.Deserialize(bytes);
        }

        public Task SaveAsync(string taskId, OutputReview review, CancellationToken ct) =>
            MissingBucketRecovery.RunAsync(
                () => _helper.PutObjectAsync(_settings.OutputBucket, KeyFor(taskId), OutputReviewJson.Serialize(review), "application/json", null, ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);

        public Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct) => ExistsAsync(MarkerKeyFor(taskId), ct);

        public Task MarkRegisteredAsync(string taskId, CancellationToken ct) => PutEmptyAsync(MarkerKeyFor(taskId), ct);

        public Task<bool> IsWithdrawnAsync(string taskId, CancellationToken ct) => ExistsAsync(WithdrawnKeyFor(taskId), ct);

        public Task MarkWithdrawnAsync(string taskId, CancellationToken ct) => PutEmptyAsync(WithdrawnKeyFor(taskId), ct);

        private async Task<bool> ExistsAsync(string key, CancellationToken ct)
        {
            var bytes = await MissingBucketRecovery.ReadAsync(
                () => _helper.GetObjectBytesAsync(_settings.OutputBucket, key, ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);
            return bytes is not null;
        }

        private Task PutEmptyAsync(string key, CancellationToken ct) =>
            MissingBucketRecovery.RunAsync(
                () => _helper.PutObjectAsync(_settings.OutputBucket, key, [], "text/plain", null, ct),
                () => _helper.CreateBucket(_settings.OutputBucket),
                _settings.OutputBucket);
    }
}
