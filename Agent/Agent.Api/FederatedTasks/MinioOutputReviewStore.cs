using Agent.Api.Models;
using FiveSafesTes.Core.Services;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Keeps review records in the TRE output bucket as "&lt;taskId&gt;.review.json".
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
    }
}
