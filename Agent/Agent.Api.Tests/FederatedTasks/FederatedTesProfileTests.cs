using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedTesProfileTests
{
    private static TesTask ValidTask() => new()
    {
        Executors =
        [
            new TesExecutor
            {
                Image = "5s-tes/federated-ops:1",
                Command = ["mean", "--column", "age", "--filter", "sex:eq:F"]
            }
        ],
        Inputs = [new TesInput { Url = "dataset://cohort_2024", Path = "/data/cohort_2024", Type = TesFileType.DIRECTORYEnum }],
        Tags = new Dictionary<string, string> { ["fed.sites"] = "tre-a", ["fed.study_id"] = "s1" }
    };

    [Fact]
    public void Parse_Valid()
    {
        var result = FederatedTesProfile.Parse(ValidTask());

        Assert.True(result.IsSuccess);
        var spec = result.Spec!;
        Assert.Equal("mean", spec.Operation);
        Assert.Equal("cohort_2024", spec.Dataset);
        Assert.Equal("age", spec.Column);
        Assert.Equal([new TaskFilter("sex", "eq", "F")], spec.Filters);
        Assert.Equal("s1", spec.StudyId);
        Assert.Equal("tre-a", spec.TreName);
    }

    [Fact]
    public void Parse_FilterValueMayContainColon()
    {
        var task = ValidTask();
        task.Executors[0].Command = ["count", "--filter", "t:eq:10:30"];

        var spec = FederatedTesProfile.Parse(task).Spec!;

        Assert.Equal("10:30", Assert.Single(spec.Filters).Value);
    }

    [Fact]
    public void Parse_CountWithoutColumn_IsValid()
    {
        var task = ValidTask();
        task.Executors[0].Command = ["count"];

        var result = FederatedTesProfile.Parse(task);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Spec!.Column);
    }

    [Fact]
    public void Parse_TagsAtLengthLimit_AreAccepted()
    {
        var task = ValidTask();
        task.Tags!["fed.sites"] = new string('t', 1024);
        task.Tags["fed.study_id"] = new string('s', 1024);

        Assert.True(FederatedTesProfile.Parse(task).IsSuccess);
    }

    [Fact]
    public void Parse_TreNameIsLowercased()
    {
        var task = ValidTask();
        task.Tags!["FED.SITES"] = "x";
        task.Tags.Remove("fed.sites");
        task.Tags["FED.SITES"] = "TRE-A";

        Assert.Equal("tre-a", FederatedTesProfile.Parse(task).Spec!.TreName);
    }

    [Fact]
    public void Parse_TreNameWithSpaces_IsAccepted()
    {
        var task = ValidTask();
        task.Tags!["fed.sites"] = "Five Safes TRE";

        Assert.Equal("five safes tre", FederatedTesProfile.Parse(task).Spec!.TreName);
    }

    [Fact]
    public void Parse_with_only_the_old_tags_is_rejected_naming_the_missing_tag()
    {
        var task = ValidTask();
        task.Tags = new Dictionary<string, string> { ["tres"] = "tre-a", ["study_id"] = "s1" };

        var result = FederatedTesProfile.Parse(task);

        Assert.Equal(FederatedTaskErrorCodes.InvalidTask, result.ErrorCode);
        Assert.Contains("fed.sites", result.ErrorMessage);
    }

    [Fact]
    public void Parse_ignores_tags_outside_the_namespace()
    {
        var task = ValidTask();
        task.Tags!["tres"] = "tre-b";
        task.Tags["study_id"] = "other";
        task.Tags["daams.request"] = "r-42";

        var spec = FederatedTesProfile.Parse(task).Spec!;

        Assert.Equal("tre-a", spec.TreName);
        Assert.Equal("s1", spec.StudyId);
    }

    [Fact]
    public void Build_writes_the_namespaced_tags()
    {
        var tags = FederatedTesProfile.Build(new FederatedTaskSpec("count", "cohort_2024", null, [], "s1", "tre-a")).Tags!;

        Assert.Equal(new Dictionary<string, string> { ["fed.sites"] = "tre-a", ["fed.study_id"] = "s1" }, tags);
    }

    public static TheoryData<string, Action<TesTask>, string> Rejections => new()
    {
        { "0 executors", t => t.Executors = [], FederatedTaskErrorCodes.InvalidTask },
        { "2 executors", t => t.Executors.Add(new TesExecutor { Image = FederatedTesProfile.ExecutorImage, Command = ["count"] }), FederatedTaskErrorCodes.InvalidTask },
        { "wrong image", t => t.Executors[0].Image = "ubuntu:22.04", FederatedTaskErrorCodes.InvalidTask },
        { "0 inputs", t => t.Inputs = [], FederatedTaskErrorCodes.InvalidTask },
        { "s3 input", t => t.Inputs![0].Url = "s3://b/k", FederatedTaskErrorCodes.InvalidTask },
        { "traversal dataset", t => t.Inputs![0].Url = "dataset://../x", FederatedTaskErrorCodes.InvalidDatasetName },
        { "uppercase dataset", t => t.Inputs![0].Url = "dataset://Cohort", FederatedTaskErrorCodes.InvalidDatasetName },
        { "output added", t => t.Outputs = [new TesOutput { Url = "s3://b/o", Path = "/o" }], FederatedTaskErrorCodes.InvalidTask },
        { "multiple sites", t => t.Tags!["fed.sites"] = "tre-a|tre-b", FederatedTaskErrorCodes.InvalidTask },
        { "study id missing", t => t.Tags!.Remove("fed.study_id"), FederatedTaskErrorCodes.InvalidTask },
        { "study id too long", t => t.Tags!["fed.study_id"] = new string('s', 1025), FederatedTaskErrorCodes.InvalidTask },
        { "sites too long", t => t.Tags!["fed.sites"] = new string('t', 1025), FederatedTaskErrorCodes.InvalidTask },
        { "unknown operation", t => t.Executors[0].Command = ["median"], FederatedTaskErrorCodes.UnknownOperation },
        { "mean without column", t => t.Executors[0].Command = ["mean"], FederatedTaskErrorCodes.InvalidTask },
        { "bad filter operator", t => t.Executors[0].Command = ["count", "--filter", "sex:like:F"], FederatedTaskErrorCodes.InvalidFilter },
        { "filter without parts", t => t.Executors[0].Command = ["count", "--filter", "sex"], FederatedTaskErrorCodes.InvalidFilter },
        { "trailing newline dataset", t => t.Inputs![0].Url = "dataset://cohort\n", FederatedTaskErrorCodes.InvalidDatasetName },
        { "null executor", t => t.Executors = [null!], FederatedTaskErrorCodes.InvalidTask },
        { "null input", t => t.Inputs = [null!], FederatedTaskErrorCodes.InvalidTask },
        { "null command", t => t.Executors[0].Command = null, FederatedTaskErrorCodes.InvalidTask },
        { "sites blank", t => t.Tags!["fed.sites"] = "  ", FederatedTaskErrorCodes.InvalidTask },
        { "tag keys collide by case", t => t.Tags = new Dictionary<string, string> { ["fed.sites"] = "a", ["FED.SITES"] = "b", ["fed.study_id"] = "s1" }, FederatedTaskErrorCodes.InvalidTask },
        { "null filter value", t => t.Executors[0].Command = ["count", "--filter", null!], FederatedTaskErrorCodes.InvalidFilter },
        { "unknown arg", t => t.Executors[0].Command = ["count", "--limit", "5"], FederatedTaskErrorCodes.InvalidTask },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Parse_Rejects(string name, Action<TesTask> change, string expectedCode)
    {
        var task = ValidTask();
        change(task);

        var result = FederatedTesProfile.Parse(task);

        Assert.False(result.IsSuccess, name);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public void Build_RoundTrips()
    {
        var spec = new FederatedTaskSpec(
            "mean", "cohort_2024", "age",
            [new TaskFilter("sex", "eq", "F"), new TaskFilter("age", "gte", "18")],
            "s1", "tre-a");

        var parsed = FederatedTesProfile.Parse(FederatedTesProfile.Build(spec)).Spec!;

        Assert.Equal(spec.Operation, parsed.Operation);
        Assert.Equal(spec.Dataset, parsed.Dataset);
        Assert.Equal(spec.Column, parsed.Column);
        Assert.Equal(spec.StudyId, parsed.StudyId);
        Assert.Equal(spec.TreName, parsed.TreName);
        Assert.True(spec.Filters.SequenceEqual(parsed.Filters));
    }
}
