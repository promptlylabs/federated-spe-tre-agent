using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

/// <summary>The rules every task meets: one executor, the dataset input and the fed.* tags. Code rules: FederatedCodeTaskProfileTests.</summary>
public class FederatedTesProfileTests
{
    private const string Code = "def site_analysis(df):\n    return {'n': len(df)}\n";

    private static TesTask ValidTask() => FederatedTesProfile.BuildCodeTask("cohort_2024", Code, "s1", "tre-a");

    private static TesInput DatasetInput(TesTask task) => task.Inputs!.Single(i => i.Path != FederatedTesProfile.CodePath);

    [Fact]
    public void Parse_Valid()
    {
        var result = FederatedTesProfile.Parse(ValidTask());

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var spec = result.Spec!;
        Assert.Equal("run-analysis", spec.Operation);
        Assert.Equal("cohort_2024", spec.Dataset);
        Assert.Equal("s1", spec.StudyId);
        Assert.Equal("tre-a", spec.TreName);
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
        task.Tags!.Remove("fed.sites");
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
        var codeSha = task.Tags![FederatedTesProfile.CodeSha256Tag];
        task.Tags = new Dictionary<string, string> { ["tres"] = "tre-a", ["study_id"] = "s1", ["fed.code_sha256"] = codeSha };

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
    public void BuildCodeTask_writes_the_namespaced_tags()
    {
        var tags = ValidTask().Tags!;

        Assert.Equal(["fed.code_sha256", "fed.sites", "fed.study_id"], tags.Keys.Order().ToArray());
        Assert.Equal("tre-a", tags["fed.sites"]);
        Assert.Equal("s1", tags["fed.study_id"]);
    }

    public static TheoryData<string, Action<TesTask>, string> Rejections => new()
    {
        { "0 executors", t => t.Executors = [], FederatedTaskErrorCodes.InvalidTask },
        { "2 executors", t => t.Executors.Add(new TesExecutor { Image = "python-runner:1", Command = ["run-analysis"] }), FederatedTaskErrorCodes.InvalidTask },
        { "0 inputs", t => t.Inputs = [], FederatedTaskErrorCodes.InvalidTask },
        { "s3 input", t => DatasetInput(t).Url = "s3://b/k", FederatedTaskErrorCodes.InvalidTask },
        { "traversal dataset", t => DatasetInput(t).Url = "dataset://../x", FederatedTaskErrorCodes.InvalidDatasetName },
        { "uppercase dataset", t => DatasetInput(t).Url = "dataset://Cohort", FederatedTaskErrorCodes.InvalidDatasetName },
        { "trailing newline dataset", t => DatasetInput(t).Url = "dataset://cohort\n", FederatedTaskErrorCodes.InvalidDatasetName },
        { "output elsewhere", t => t.Outputs = [new TesOutput { Url = "s3://b/o", Path = "/o" }], FederatedTaskErrorCodes.InvalidTask },
        { "multiple sites", t => t.Tags!["fed.sites"] = "tre-a|tre-b", FederatedTaskErrorCodes.InvalidTask },
        { "study id missing", t => t.Tags!.Remove("fed.study_id"), FederatedTaskErrorCodes.InvalidTask },
        { "study id too long", t => t.Tags!["fed.study_id"] = new string('s', 1025), FederatedTaskErrorCodes.InvalidTask },
        { "sites too long", t => t.Tags!["fed.sites"] = new string('t', 1025), FederatedTaskErrorCodes.InvalidTask },
        { "sites blank", t => t.Tags!["fed.sites"] = "  ", FederatedTaskErrorCodes.InvalidTask },
        { "tag keys collide by case", t => t.Tags!["FED.SITES"] = "b", FederatedTaskErrorCodes.InvalidTask },
        { "null executor", t => t.Executors = [null!], FederatedTaskErrorCodes.InvalidTask },
        { "null input", t => t.Inputs = [null!, null!], FederatedTaskErrorCodes.InvalidTask },
        { "null command", t => t.Executors[0].Command = null, FederatedTaskErrorCodes.InvalidTask },
        // The removed count/mean shape: one dataset input, no outputs, an operation as the command.
        { "count/mean command", t => t.Executors[0].Command = ["count"], FederatedTaskErrorCodes.InvalidTask },
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
}
