using FiveSafesTes.Core.Services;
using Serilog;
using Submission.Api.Models;

namespace Submission.Api.Services
{
    public class RustFsFederatedResultStore : IFederatedResultStore
    {
        private readonly IMinioHelper _minio;

        // Own helper on its own settings: never the DI IMinioHelper, which is the researcher-facing project store.
        public RustFsFederatedResultStore(FederatedResultsStorageSettings settings)
        {
            Bucket = settings.Bucket;
            _minio = new MinioHelper(settings.ToMinioSettings());
        }

        public string Bucket { get; }

        public async Task<bool> EnsureReadyAsync()
        {
            try
            {
                if (!await _minio.CreateBucket(Bucket))
                {
                    Log.Error("Federated results bucket {Bucket} could not be created or found", Bucket);
                    return false;
                }

                if (await _minio.HasBucketPolicyAsync(Bucket))
                {
                    Log.Error("Federated results bucket {Bucket} has a bucket policy; it must have none", Bucket);
                }

                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Federated results bucket {Bucket} is not ready", Bucket);
                return false;
            }
        }

        public Task PutAsync(string key, byte[] content, CancellationToken ct) =>
            _minio.PutObjectAsync(Bucket, key, content, "application/json", null, ct);

        public Task<byte[]?> GetAsync(string key, CancellationToken ct) =>
            _minio.GetObjectBytesAsync(Bucket, key, ct);
    }
}
