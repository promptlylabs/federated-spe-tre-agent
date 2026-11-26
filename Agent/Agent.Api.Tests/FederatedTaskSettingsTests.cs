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
            ["FederatedTaskSettings:ApprovedImages:0"] = " python-runner:1 ",
            ["FederatedTaskSettings:ApprovedImages:1"] = "  ",
        });

        Assert.Equal(["python-runner:1"], settings.ApprovedImages);
    }

    [Fact]
    public void Image_not_approved_is_an_input_check_code() =>
        Assert.Contains("image_not_approved", FederatedTaskErrorCodes.InputCheckCodes);

    [Fact]
    public void ApprovedImages_set_directly_are_trimmed_and_blank_entries_dropped()
    {
        var settings = new FederatedTaskSettings { ApprovedImages = [" python-runner:1 ", "", "  "] };

        Assert.Equal(["python-runner:1"], settings.ApprovedImages);
    }

    [Fact]
    public void FromConfiguration_ApprovedImages_as_one_comma_separated_value_is_split()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["FederatedTaskSettings:ApprovedImages"] = "python-runner:1, other:2",
        });

        Assert.Equal(["python-runner:1", "other:2"], settings.ApprovedImages);
    }

    [Fact]
    public void CodeRunDefaults()
    {
        var s = new FederatedTaskSettings();
        Assert.Equal(30, s.MaxRunMinutes);
        Assert.Equal(1, s.DefaultCpuCores);
        Assert.Equal(4, s.MaxCpuCores);
        Assert.Equal(8, s.DefaultRamGb);
        Assert.Equal(16, s.MaxRamGb);
        Assert.Equal(1, s.DefaultDiskGb);
        Assert.Equal(4, s.MaxDiskGb);
        Assert.Equal(2, s.MaxConcurrentRuns);
        Assert.Equal("unix:///var/run/docker.sock", s.DockerEndpoint);
    }

    [Theory]
    [InlineData("python-runner:1")]
    [InlineData("registry.example/spms/python-runner:1")]
    public void ApprovedImagesProblem_NullForKnownImageNames(string image) =>
        Assert.Null(new FederatedTaskSettings { ApprovedImages = [image] }.ApprovedImagesProblem());

    [Fact]
    public void ApprovedImagesProblem_FlagsTypo() =>
        Assert.NotNull(new FederatedTaskSettings { ApprovedImages = ["pyhton-runner:1"] }.ApprovedImagesProblem());

    [Fact]
    public void ApprovedImagesProblem_is_null_when_the_list_is_empty_or_has_the_runner_image()
    {
        Assert.Null(new FederatedTaskSettings().ApprovedImagesProblem());
        Assert.Null(new FederatedTaskSettings { ApprovedImages = ["other:1", "python-runner:1"] }.ApprovedImagesProblem());
    }

    [Fact]
    public void ApprovedImagesProblem_names_the_runner_image_when_the_list_lacks_it()
    {
        var warning = new FederatedTaskSettings { ApprovedImages = ["docker.io/spms/python-runnr:1"] }.ApprovedImagesProblem();

        Assert.NotNull(warning);
        Assert.Contains("python-runner", warning);
        Assert.Contains("image_not_approved", warning);
    }

    [Fact]
    public void ApprovedImagesProblem_IgnoresLeftoverOpsImage()
    {
        // Names the old image on purpose: an upgraded hospital whose list still has only the old image must get the startup error.
        Assert.NotNull(new FederatedTaskSettings { ApprovedImages = ["5s-tes/federated-ops:1"] }.ApprovedImagesProblem());
        Assert.Null(new FederatedTaskSettings { ApprovedImages = ["5s-tes/federated-ops:1", "python-runner:1"] }.ApprovedImagesProblem());
    }

    [Fact]
    public void FromConfiguration_single_ApprovedImages_value_replaces_indexed_entries()
    {
        // appsettings lists ApprovedImages:0; an environment variable FederatedTaskSettings__ApprovedImages overrides it.
        var settings = Bind(new Dictionary<string, string?>
        {
            ["FederatedTaskSettings:ApprovedImages:0"] = "python-runner:1",
            ["FederatedTaskSettings:ApprovedImages"] = "other:1",
        });

        Assert.Equal(["other:1"], settings.ApprovedImages);
    }

    [Fact]
    public void RunSettingsProblem_NullForDefaults() =>
        Assert.Null(new FederatedTaskSettings().RunSettingsProblem());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RunSettingsProblem_FlagsMaxConcurrentRunsZeroOrLess(int value)
    {
        var problem = new FederatedTaskSettings { MaxConcurrentRuns = value }.RunSettingsProblem();

        Assert.NotNull(problem);
        Assert.Contains("FederatedTaskSettings:MaxConcurrentRuns", problem);
        Assert.DoesNotContain("MaxRunMinutes", problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RunSettingsProblem_FlagsMaxRunMinutesZeroOrLess(int value)
    {
        var problem = new FederatedTaskSettings { MaxRunMinutes = value }.RunSettingsProblem();

        Assert.NotNull(problem);
        Assert.Contains("FederatedTaskSettings:MaxRunMinutes", problem);
        Assert.DoesNotContain("MaxConcurrentRuns", problem);
    }

    [Fact]
    public void RunSettingsProblem_NamesBothWhenBothAreWrong()
    {
        var problem = new FederatedTaskSettings { MaxConcurrentRuns = 0, MaxRunMinutes = 0 }.RunSettingsProblem();

        Assert.NotNull(problem);
        Assert.Contains("MaxConcurrentRuns", problem);
        Assert.Contains("MaxRunMinutes", problem);
    }
}
