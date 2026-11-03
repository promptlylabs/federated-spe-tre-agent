using FiveSafesTes.Core.Models.ViewModels;
using FiveSafesTes.Core.Services;
using Xunit;

namespace Agent.Api.Tests;

public class MinioHelperPolicyTests
{
    [Fact]
    public async Task PutCannedPolicy_StoreUnreachable_ReturnsFalse()
    {
        var helper = new MinioHelper(new MinioSettings
        {
            Url = "http://127.0.0.1:1",
            AccessKey = "a",
            SecretKey = "b",
            AWSService = "s3",
            AWSRegion = "us-east-1"
        });

        Assert.False(await helper.PutCannedPolicyAsync("p", "{}"));
    }
}
