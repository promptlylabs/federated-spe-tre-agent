using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
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

    [Fact]
    public void Defaults_include_output_bucket_and_read_policy()
    {
        var settings = Bind(new Dictionary<string, string?>());

        Assert.Equal("federated-outputs", settings.OutputBucket);
        Assert.Equal("federated-outputs-read", settings.OutputReadPolicyName);
    }

    [Fact]
    public void FromConfiguration_ApprovedImages_unset_is_empty() =>
        Assert.Empty(Bind(new Dictionary<string, string?>()).ApprovedImages);

    [Fact]
    public void FromConfiguration_ApprovedImages_are_trimmed_and_blank_entries_dropped()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["FederatedTaskSettings:ApprovedImages:0"] = " 5s-tes/federated-ops:1 ",
            ["FederatedTaskSettings:ApprovedImages:1"] = "  ",
        });

        Assert.Equal(["5s-tes/federated-ops:1"], settings.ApprovedImages);
    }

    [Fact]
    public void Image_not_approved_is_an_input_check_code() =>
        Assert.Contains("image_not_approved", FederatedTaskErrorCodes.InputCheckCodes);

    [Fact]
    public void ApprovedImages_set_directly_are_trimmed_and_blank_entries_dropped()
    {
        var settings = new FederatedTaskSettings { ApprovedImages = [" 5s-tes/federated-ops:1 ", "", "  "] };

        Assert.Equal(["5s-tes/federated-ops:1"], settings.ApprovedImages);
    }

    [Fact]
    public void FromConfiguration_ApprovedImages_as_one_comma_separated_value_is_split()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["FederatedTaskSettings:ApprovedImages"] = "5s-tes/federated-ops:1, other:2",
        });

        Assert.Equal(["5s-tes/federated-ops:1", "other:2"], settings.ApprovedImages);
    }

    [Fact]
    public void ApprovedImagesProblem_is_null_when_the_list_is_empty_or_has_the_profile_image()
    {
        Assert.Null(new FederatedTaskSettings().ApprovedImagesProblem());
        Assert.Null(new FederatedTaskSettings { ApprovedImages = ["other:1", FederatedTesProfile.ExecutorImage] }.ApprovedImagesProblem());
    }

    [Fact]
    public void ApprovedImagesProblem_names_the_profile_image_when_the_list_lacks_it()
    {
        var warning = new FederatedTaskSettings { ApprovedImages = ["docker.io/5s-tes/federated-ops:1"] }.ApprovedImagesProblem();

        Assert.NotNull(warning);
        Assert.Contains("'5s-tes/federated-ops:1'", warning);
        Assert.Contains("image_not_approved", warning);
    }

    [Fact]
    public void FromConfiguration_single_ApprovedImages_value_replaces_indexed_entries()
    {
        // appsettings lists ApprovedImages:0; an environment variable FederatedTaskSettings__ApprovedImages overrides it.
        var settings = Bind(new Dictionary<string, string?>
        {
            ["FederatedTaskSettings:ApprovedImages:0"] = "5s-tes/federated-ops:1",
            ["FederatedTaskSettings:ApprovedImages"] = "other:1",
        });

        Assert.Equal(["other:1"], settings.ApprovedImages);
    }
}
