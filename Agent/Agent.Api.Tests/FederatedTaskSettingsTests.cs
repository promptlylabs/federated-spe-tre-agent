using Agent.Api.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agent.Api.Tests;

public class FederatedTaskSettingsTests
{
    private static FederatedTaskSettings Bind(Dictionary<string, string?> values) =>
        FederatedTaskSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void FromConfiguration_Unset_UsesCsv()
    {
        var settings = Bind(new Dictionary<string, string?>());

        Assert.Equal(["csv"], settings.AllowedExtensions);
        Assert.Equal("federated-datasets", settings.DatasetBucket);
    }

    [Fact]
    public void FromConfiguration_ConfiguredExtensions_ReplaceTheDefault()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["FederatedTaskSettings:AllowedExtensions:0"] = "sql",
            ["FederatedTaskSettings:DatasetBucket"] = "other"
        });

        Assert.Equal(["sql"], settings.AllowedExtensions);
        Assert.Equal("other", settings.DatasetBucket);
    }
}
