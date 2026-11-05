using Microsoft.Extensions.Configuration;
using Submission.Api.Models;
using Xunit;

namespace Submission.Api.Tests;

public class FederatedResultsStorageSettingsTests
{
    [Fact]
    public void Settings_defaults_bucket_and_binds_section()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Equal("federated-results", FederatedResultsStorageSettings.FromConfiguration(empty).Bucket);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FederatedResultsStorageSettings:Url"] = "http://rustfs:9000",
            ["FederatedResultsStorageSettings:AccessKey"] = "ak",
            ["FederatedResultsStorageSettings:SecretKey"] = "sk"
        }).Build();

        var settings = FederatedResultsStorageSettings.FromConfiguration(config);
        Assert.Equal("http://rustfs:9000", settings.Url);

        var minio = settings.ToMinioSettings();
        Assert.Equal("http://rustfs:9000", minio.Url);
        Assert.Equal("ak", minio.AccessKey);
        Assert.Equal("sk", minio.SecretKey);
        Assert.Equal("federated-results", minio.BucketName);
        Assert.Equal("us-east-1", minio.AWSRegion);
    }
}
