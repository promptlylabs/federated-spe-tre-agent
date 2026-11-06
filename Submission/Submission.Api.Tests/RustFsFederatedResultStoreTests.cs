using System.Reflection;
using FiveSafesTes.Core.Services;
using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests
{
    public class RustFsFederatedResultStoreTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public async Task EnsureReady_uses_only_bucket_scoped_calls(bool? hasPolicy)
        {
            var minio = MinioStub.Create(ensureBucket: true, hasPolicy: hasPolicy);

            var ready = await new RustFsFederatedResultStore("federated-results", minio).EnsureReadyAsync();

            Assert.True(ready);
            var stub = (MinioStub)(object)minio;
            Assert.Equal(new[] { "EnsureBucketAsync", "HasBucketPolicyAsync" }, stub.Calls);
        }

        [Fact]
        public async Task EnsureReady_is_false_when_the_bucket_cannot_be_found_or_created()
        {
            var minio = MinioStub.Create(ensureBucket: false, hasPolicy: false);

            var ready = await new RustFsFederatedResultStore("federated-results", minio).EnsureReadyAsync();

            Assert.False(ready);
            Assert.Equal(new[] { "EnsureBucketAsync" }, ((MinioStub)(object)minio).Calls);
        }

        /// <summary>Records calls; any IMinioHelper method other than the two stubbed ones fails the test.</summary>
        public class MinioStub : DispatchProxy
        {
            public List<string> Calls { get; } = new();
            private bool _ensureBucket;
            private bool? _hasPolicy;

            public static IMinioHelper Create(bool ensureBucket, bool? hasPolicy)
            {
                var proxy = Create<IMinioHelper, MinioStub>();
                var stub = (MinioStub)(object)proxy;
                stub._ensureBucket = ensureBucket;
                stub._hasPolicy = hasPolicy;
                return proxy;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                Calls.Add(targetMethod!.Name);
                return targetMethod.Name switch
                {
                    nameof(IMinioHelper.EnsureBucketAsync) => Task.FromResult(_ensureBucket),
                    nameof(IMinioHelper.HasBucketPolicyAsync) => Task.FromResult(_hasPolicy),
                    _ => throw new InvalidOperationException($"unexpected call to {targetMethod.Name}")
                };
            }
        }
    }
}
