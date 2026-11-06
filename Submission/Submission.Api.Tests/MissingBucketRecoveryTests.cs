using Amazon.S3;
using FiveSafesTes.Core.Services;
using Xunit;

namespace Submission.Api.Tests;

public class MissingBucketRecoveryTests
{
    private static AmazonS3Exception NoBucket() => new("missing") { ErrorCode = "NoSuchBucket" };

    [Fact]
    public async Task Write_creates_bucket_and_retries_once_on_NoSuchBucket()
    {
        var calls = 0;
        var created = 0;

        await MissingBucketRecovery.RunAsync(
            () => ++calls == 1 ? throw NoBucket() : Task.CompletedTask,
            () => { created++; return Task.FromResult(true); },
            "b");

        Assert.Equal(2, calls);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Write_does_not_create_bucket_when_it_succeeds()
    {
        var created = 0;

        await MissingBucketRecovery.RunAsync(
            () => Task.CompletedTask, () => { created++; return Task.FromResult(true); }, "b");

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Write_retries_only_once()
    {
        var calls = 0;

        await Assert.ThrowsAsync<AmazonS3Exception>(() => MissingBucketRecovery.RunAsync(
            () => { calls++; throw NoBucket(); },
            () => Task.FromResult(true), "b"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Write_rethrows_original_when_bucket_cannot_be_created()
    {
        var calls = 0;

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => MissingBucketRecovery.RunAsync(
            () => { calls++; throw NoBucket(); },
            () => Task.FromResult(false), "b"));

        Assert.Equal("NoSuchBucket", ex.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Other_errors_propagate_without_creating_a_bucket()
    {
        var created = 0;

        await Assert.ThrowsAsync<AmazonS3Exception>(() => MissingBucketRecovery.RunAsync(
            () => throw new AmazonS3Exception("denied") { ErrorCode = "AccessDenied" },
            () => { created++; return Task.FromResult(true); }, "b"));
        await Assert.ThrowsAsync<IOException>(() => MissingBucketRecovery.RunAsync(
            () => throw new IOException("x"),
            () => { created++; return Task.FromResult(true); }, "b"));

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Read_creates_bucket_and_returns_null_on_NoSuchBucket()
    {
        var created = 0;

        var result = await MissingBucketRecovery.ReadAsync<byte[]>(
            () => throw NoBucket(), () => { created++; return Task.FromResult(true); }, "b");

        Assert.Null(result);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Read_returns_value_and_skips_creation_when_bucket_exists()
    {
        var created = 0;

        var result = await MissingBucketRecovery.ReadAsync<byte[]>(
            () => Task.FromResult<byte[]?>(new byte[] { 1 }), () => { created++; return Task.FromResult(true); }, "b");

        Assert.Equal(new byte[] { 1 }, result);
        Assert.Equal(0, created);
    }
}
