using Amazon.S3;
using Serilog;

namespace FiveSafesTes.Core.Services
{
    /// <summary>
    /// Recovers from a bucket that went missing after startup: on <c>NoSuchBucket</c> the bucket is created once and
    /// the operation is retried once. Every other error propagates.
    /// </summary>
    public static class MissingBucketRecovery
    {
        private static bool IsNoSuchBucket(AmazonS3Exception ex) => ex.ErrorCode == "NoSuchBucket";

        /// <summary>Runs <paramref name="operation"/>; on NoSuchBucket creates the bucket and runs it once more.</summary>
        public static async Task<T> RunAsync<T>(Func<Task<T>> operation, Func<Task<bool>> createBucket, string bucket)
        {
            try
            {
                return await operation();
            }
            catch (AmazonS3Exception ex) when (IsNoSuchBucket(ex))
            {
                Log.Warning("Bucket {Bucket} is missing; creating it and retrying once", bucket);
                if (!await createBucket())
                {
                    throw;
                }

                return await operation();
            }
        }

        /// <summary>As <see cref="RunAsync{T}"/> for an operation without a result.</summary>
        public static Task RunAsync(Func<Task> operation, Func<Task<bool>> createBucket, string bucket) =>
            RunAsync(async () =>
            {
                await operation();
                return true;
            }, createBucket, bucket);

        /// <summary>
        /// For a read: on NoSuchBucket creates the bucket and returns <c>default</c>, because a new bucket holds nothing.
        /// </summary>
        public static async Task<T?> ReadAsync<T>(Func<Task<T?>> operation, Func<Task<bool>> createBucket, string bucket)
        {
            try
            {
                return await operation();
            }
            catch (AmazonS3Exception ex) when (IsNoSuchBucket(ex))
            {
                Log.Warning("Bucket {Bucket} is missing; creating it", bucket);
                if (!await createBucket())
                {
                    throw;
                }

                return default;
            }
        }
    }
}
