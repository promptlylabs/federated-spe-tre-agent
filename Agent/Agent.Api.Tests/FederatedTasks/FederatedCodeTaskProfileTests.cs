using System.Security.Cryptography;
using System.Text;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedCodeTaskProfileTests
{
    private const string Code = "def site_analysis(df):\n    return {'n': len(df)}\n";

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static FederatedIntakeLimits Limits(params string[] images) =>
        new(images.Length == 0 ? ["python-runner:1"] : images, 4, 16, 20);

    private static TesTask ValidCodeTask(string code = Code) => new()
    {
        Executors =
        [
            new TesExecutor
            {
                Image = "python-runner:1",
                Command = ["run-analysis", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"]
            }
        ],
        Inputs =
        [
            new TesInput { Path = "/code/analysis.py", Type = TesFileType.FILEEnum, Content = code },
            new TesInput { Url = "dataset://cohort_2024", Path = "/data/cohort_2024", Type = TesFileType.DIRECTORYEnum }
        ],
        Outputs = [new TesOutput { Path = "/outputs/result.json", Url = "result://", Type = TesFileType.FILEEnum }],
        Resources = new TesResources { CpuCores = 2, RamGb = 8, DiskGb = 10 },
        Tags = new Dictionary<string, string>
        {
            ["fed.sites"] = "tre-a",
            ["fed.study_id"] = "s1",
            ["fed.code_sha256"] = Sha(code)
        }
    };

    [Fact]
    public void Valid_code_task_parses()
    {
        var result = FederatedTesProfile.Parse(ValidCodeTask(), Limits());

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var spec = result.Spec!;
        Assert.True(spec.IsCodeTask);
        Assert.Equal("run-analysis", spec.Operation);
        Assert.Equal("cohort_2024", spec.Dataset);
        Assert.Null(spec.Column);
        Assert.Empty(spec.Filters);
        Assert.Equal(Sha(Code), spec.CodeSha256);
        Assert.Equal("tre-a", spec.TreName);
        Assert.Equal("s1", spec.StudyId);
    }

    [Fact]
    public void Valid_code_task_parses_without_limits()
    {
        var task = ValidCodeTask();
        task.Executors[0].Image = "my/other:1";

        var result = FederatedTesProfile.Parse(task);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(result.Spec!.IsCodeTask);
    }

    public static TheoryData<string, Action<TesTask>, string> Violations => new()
    {
        { "image not approved", t => t.Executors[0].Image = "python-runner:2",
            "executor image must be 5s-tes/federated-ops:1 or an approved runner image" },
        { "env set", t => t.Executors[0].Env = new Dictionary<string, string> { ["PYTHONPATH"] = "/x" },
            "executor env, workdir, stdin, stdout and stderr must not be set" },
        { "workdir set", t => t.Executors[0].Workdir = "/tmp",
            "executor env, workdir, stdin, stdout and stderr must not be set" },
        { "three inputs", t => t.Inputs.Add(new TesInput { Path = "/code/other.py", Type = TesFileType.FILEEnum, Content = "x" }),
            "task must have one code input and one dataset input" },
        { "no code input", t => t.Inputs[0].Path = "/code/main.py",
            "task must have one code input and one dataset input" },
        { "code input with url", t => t.Inputs[0].Url = "https://example.org/analysis.py",
            "the code input must have content and no url" },
        { "code input empty", t => t.Inputs[0].Content = "",
            "the code input must have content and no url" },
        { "code input is a directory", t => t.Inputs[0].Type = TesFileType.DIRECTORYEnum,
            "the code input must be a FILE" },
        { "dataset path differs", t => t.Inputs[1].Path = "/data/other",
            "the dataset input path must be /data/cohort_2024" },
        { "--debug", t => t.Executors[0].Command = ["run-analysis", "--debug", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"],
            "executor command must be run-analysis /code/analysis.py /data/<dataset> /outputs/result.json" },
        { "wrong dataset path in command", t => t.Executors[0].Command![2] = "/data/other",
            "executor command must be run-analysis /code/analysis.py /data/<dataset> /outputs/result.json" },
        { "other command", t => t.Executors[0].Command = ["python3", "/code/analysis.py"],
            "executor command must be run-analysis /code/analysis.py /data/<dataset> /outputs/result.json" },
        { "two outputs", t => t.Outputs!.Add(new TesOutput { Path = "/outputs/x.json", Url = "result://", Type = TesFileType.FILEEnum }),
            "task must have exactly one output, /outputs/result.json" },
        { "other output path", t => t.Outputs![0].Path = "/outputs/x.json",
            "task must have exactly one output, /outputs/result.json" },
        { "no outputs", t => t.Outputs = [],
            "task must have exactly one output, /outputs/result.json" },
        { "too many cores", t => t.Resources!.CpuCores = 5, "resources.cpu_cores must be more than 0 and at most 4" },
        { "too much ram", t => t.Resources!.RamGb = 17, "resources.ram_gb must be more than 0 and at most 16" },
        { "too much disk", t => t.Resources!.DiskGb = 21, "resources.disk_gb must be more than 0 and at most 20" },
        { "no code hash", t => t.Tags!.Remove("fed.code_sha256"),
            "tag 'fed.code_sha256' must be a lowercase hex SHA-256" },
        { "hash of other code", t => t.Tags!["fed.code_sha256"] = Sha("print(1)\n"),
            "tag 'fed.code_sha256' does not match the code" },
        { "no study id", t => t.Tags!.Remove("fed.study_id"), "tag 'fed.study_id' is required" },
        { "output url elsewhere", t => t.Outputs![0].Url = "https://example.org/upload", "the output url must be result://" },
        { "output url missing", t => t.Outputs![0].Url = null!, "the output url must be result://" },
        { "null code hash", t => t.Tags!["fed.code_sha256"] = null!,
            "tag 'fed.code_sha256' must be a lowercase hex SHA-256" },
        { "volumes", t => t.Volumes = ["/data/c1", "/outputs"], "task must not declare volumes" },
        { "dataset input with content", t => t.Inputs[1].Content = "age\n1\n", "the dataset input must not have content" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void Rule_violations_are_invalid_task(string name, Action<TesTask> breakIt, string message)
    {
        var task = ValidCodeTask();
        breakIt(task);

        var result = FederatedTesProfile.Parse(task, Limits());

        Assert.False(result.IsSuccess, name);
        Assert.Equal("invalid_task", result.ErrorCode);
        Assert.Equal(message, result.ErrorMessage);
    }

    [Fact]
    public void Empty_approved_list_rejects_every_code_task()
    {
        var result = FederatedTesProfile.Parse(ValidCodeTask(), new FederatedIntakeLimits([], 4, 16, 20));

        Assert.Equal("invalid_task", result.ErrorCode);
        Assert.Equal("no runner image is approved", result.ErrorMessage);
    }

    [Fact]
    public void Invalid_dataset_name_in_code_task()
    {
        var task = ValidCodeTask();
        task.Inputs[1].Url = "dataset://Bad Name";

        var result = FederatedTesProfile.Parse(task, Limits());

        Assert.Equal("invalid_dataset_name", result.ErrorCode);
    }

    [Fact]
    public void Code_over_256_KiB_is_rejected()
    {
        var atLimit = FederatedTesProfile.Parse(ValidCodeTask(new string('a', 262144)), Limits());
        var over = FederatedTesProfile.Parse(ValidCodeTask(new string('a', 262145)), Limits());

        Assert.True(atLimit.IsSuccess, atLimit.ErrorMessage);
        Assert.Equal("invalid_task", over.ErrorCode);
        Assert.Equal("code must be at most 262144 bytes", over.ErrorMessage);
    }

    [Fact]
    public void Code_size_and_hash_use_utf8_bytes()
    {
        var accented = FederatedTesProfile.Parse(ValidCodeTask("# é\n" + Code), Limits());
        // 131073 chars, 262146 UTF-8 bytes.
        var tooLong = FederatedTesProfile.Parse(ValidCodeTask(new string('é', 131073)), Limits());

        Assert.True(accented.IsSuccess, accented.ErrorMessage);
        Assert.Equal("code must be at most 262144 bytes", tooLong.ErrorMessage);
    }

    [Fact]
    public void Uppercase_code_sha256_is_rejected()
    {
        var task = ValidCodeTask();
        task.Tags!["fed.code_sha256"] = Sha(Code).ToUpperInvariant();

        var result = FederatedTesProfile.Parse(task, Limits());

        Assert.Equal("tag 'fed.code_sha256' must be a lowercase hex SHA-256", result.ErrorMessage);
    }

    [Fact]
    public void Inputs_in_either_order_are_accepted()
    {
        var task = ValidCodeTask();
        task.Inputs.Reverse();

        var result = FederatedTesProfile.Parse(task, Limits());

        Assert.True(result.IsSuccess, result.ErrorMessage);
    }

    [Fact]
    public void Resources_may_be_absent()
    {
        var task = ValidCodeTask();
        task.Resources = null;

        Assert.True(FederatedTesProfile.Parse(task, Limits()).IsSuccess);
    }

    [Fact]
    public void Count_task_is_unchanged()
    {
        var task = new TesTask
        {
            Executors = [new TesExecutor { Image = "5s-tes/federated-ops:1", Command = ["mean", "--column", "age"] }],
            Inputs = [new TesInput { Url = "dataset://cohort_2024", Path = "/data/cohort_2024", Type = TesFileType.DIRECTORYEnum }],
            Tags = new Dictionary<string, string> { ["fed.sites"] = "tre-a", ["fed.study_id"] = "s1" }
        };

        var withLimits = FederatedTesProfile.Parse(task, Limits());
        var without = FederatedTesProfile.Parse(task);

        Assert.True(withLimits.IsSuccess);
        Assert.Equal(without.Spec! with { Filters = [] }, withLimits.Spec! with { Filters = [] });
        Assert.Empty(withLimits.Spec!.Filters);
        Assert.Equal("mean", withLimits.Spec!.Operation);
        Assert.Null(withLimits.Spec.CodeSha256);
        Assert.False(withLimits.Spec.IsCodeTask);
    }
}
