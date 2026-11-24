using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

/// <summary>
/// The ticket's scenarios against real Docker and the runner image. Each test uses its own task id and checks that
/// nothing labelled with it is left once the run is removed. The image is amd64, so on Apple silicon each run is slow.
/// </summary>
public class DockerCodeRunnerTests
{
    private const string Dataset = "cohort";
    private const long MiB = 1024L * 1024;
    private static readonly TimeSpan WaitCap = TimeSpan.FromMinutes(3);

    private static readonly string Examples = Path.Combine(FindRepoRoot(), "runner", "examples");
    private static readonly byte[] CohortCsv = File.ReadAllBytes(Path.Combine(Examples, "cohort", "cohort.csv"));
    private static readonly byte[] CohortSchema = File.ReadAllBytes(Path.Combine(Examples, "cohort", "schema.json"));

    private readonly IDockerClient _docker = DockerFactAttribute.CreateClient();
    private readonly FederatedTaskSettings _settings = new();

    private DockerCodeRunner Runner() => new(_docker, _settings);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "runner", "examples")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("runner/examples not found above " + AppContext.BaseDirectory);
    }

    private static string NewTaskId() => Guid.NewGuid().ToString("D");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static CodeRunRequest Request(string taskId, string code, byte[]? csv = null) =>
        new(taskId, DockerFactAttribute.RunnerImage, Dataset, Sha(csv ?? CohortCsv),
            [
                new StagedFile($"data/{Dataset}/cohort.csv", new MemoryStream(csv ?? CohortCsv)),
                new StagedFile($"data/{Dataset}/schema.json", new MemoryStream(CohortSchema)),
                new StagedFile("code/analysis.py", new MemoryStream(Encoding.UTF8.GetBytes(code)))
            ],
            1, 2048 * MiB, 256 * MiB);

    private static async Task<CodeRunState> WaitForAsync(ICodeRunner runner, string taskId, CodeRunStatus status)
    {
        var deadline = DateTime.UtcNow + WaitCap;
        while (true)
        {
            var state = await runner.GetAsync(taskId, CancellationToken.None);
            if (state.Status == status)
            {
                return state;
            }

            Assert.True(DateTime.UtcNow < deadline, $"The run did not reach {status} within {WaitCap}; it is {state.Status}");
            await Task.Delay(500);
        }
    }

    private async Task<(CodeRunState State, JsonElement Result)> RunToEndAsync(DockerCodeRunner runner, CodeRunRequest request)
    {
        await runner.StartAsync(request, CancellationToken.None);
        var state = await WaitForAsync(runner, request.TaskId, CodeRunStatus.Exited);
        var read = await runner.ReadResultAsync(request.TaskId, CodeExecution.MaxResultFileBytes, CancellationToken.None);
        Assert.False(read.TooLarge);
        Assert.NotNull(read.Bytes);
        return (state, JsonDocument.Parse(read.Bytes).RootElement.Clone());
    }

    /// <summary>Removes the run, then checks that no run, container or volume labelled with the task id is left.</summary>
    private async Task RemoveAndAssertNothingLeftAsync(DockerCodeRunner runner, string taskId)
    {
        await runner.RemoveAsync(taskId, CancellationToken.None);
        await runner.RemoveAsync(taskId, CancellationToken.None); // safe to repeat

        Assert.DoesNotContain(await runner.ListAsync(CancellationToken.None), r => r.TaskId == taskId);
        Assert.Equal(CodeRunState.None, await runner.GetAsync(taskId, CancellationToken.None));
        await AssertNothingLabelledAsync(taskId);
    }

    private async Task AssertNothingLabelledAsync(string taskId)
    {
        var label = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [$"fed.task-id={taskId}"] = true }
        };
        var containers = await _docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = true, Filters = label });
        Assert.Empty(containers);
        var volumes = await _docker.Volumes.ListAsync(new VolumesListParameters { Filters = label });
        Assert.Empty(volumes.Volumes ?? []);
    }

    [DockerFact]
    public async Task Example_ProducesOkResult()
    {
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var code = await File.ReadAllTextAsync(Path.Combine(Examples, "analysis.py"));
            var (state, result) = await RunToEndAsync(runner, Request(taskId, code));

            Assert.Equal(0, state.ExitCode);
            Assert.False(state.OomKilled);
            Assert.NotNull(state.StartedAt);
            Assert.NotNull(state.FinishedAt);
            Assert.True(state.FinishedAt >= state.StartedAt);
            Assert.Equal(Sha(CohortCsv), state.DatasetSha256);
            Assert.Contains(await runner.ListAsync(CancellationToken.None), r => r.TaskId == taskId);

            Assert.Equal("ok", result.GetProperty("status").GetString());
            Assert.Equal(3, result.GetProperty("n").GetInt32());
            Assert.Equal(40.0, result.GetProperty("result").GetProperty("mean_age").GetDouble());
            Assert.Equal(Sha(CohortCsv), result.GetProperty("datasetSha256").GetString());
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task NetworkCall_Fails()
    {
        const string code = """
            import socket

            def site_analysis(df):
                try:
                    socket.create_connection(("1.1.1.1", 53), timeout=5).close()
                    connected = True
                except OSError:
                    connected = False
                return {"n": 0, "connected": connected}
            """;
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var (_, result) = await RunToEndAsync(runner, Request(taskId, code));

            Assert.Equal("ok", result.GetProperty("status").GetString());
            Assert.False(result.GetProperty("result").GetProperty("connected").GetBoolean());
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task WriteToDataset_Fails_DataUnchanged()
    {
        // Tries to change the dataset, then hashes it in the same run: a later run would stage the file again.
        const string code = """
            import hashlib

            def site_analysis(df):
                try:
                    with open("/data/cohort/cohort.csv", "a") as f:
                        f.write("99,F\n")
                    write_failed = False
                except OSError:
                    write_failed = True
                try:
                    open("/data/cohort/new.csv", "w").close()
                    create_failed = False
                except OSError:
                    create_failed = True
                with open("/data/cohort/cohort.csv", "rb") as f:
                    sha = hashlib.sha256(f.read()).hexdigest()
                return {"n": 0, "write_failed": write_failed, "create_failed": create_failed, "sha": sha}
            """;
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var (_, result) = await RunToEndAsync(runner, Request(taskId, code));

            Assert.Equal("ok", result.GetProperty("status").GetString());
            var value = result.GetProperty("result");
            Assert.True(value.GetProperty("write_failed").GetBoolean());
            Assert.True(value.GetProperty("create_failed").GetBoolean());
            Assert.Equal(Sha(CohortCsv), value.GetProperty("sha").GetString());
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task NotRoot_ReadOnlyRoot()
    {
        const string code = """
            import os

            def site_analysis(df):
                try:
                    open("/etc/x", "w").close()
                    etc_write_failed = False
                except OSError:
                    etc_write_failed = True
                try:
                    open("/code/x", "w").close()
                    code_write_failed = False
                except OSError:
                    code_write_failed = True
                return {"n": 0, "uid": os.getuid(), "gid": os.getgid(), "etc_write_failed": etc_write_failed,
                        "code_write_failed": code_write_failed}
            """;
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var (_, result) = await RunToEndAsync(runner, Request(taskId, code));

            Assert.Equal("ok", result.GetProperty("status").GetString());
            var value = result.GetProperty("result");
            Assert.Equal(1001, value.GetProperty("uid").GetInt32());
            Assert.Equal(0, value.GetProperty("gid").GetInt32());
            Assert.True(value.GetProperty("etc_write_failed").GetBoolean());
            Assert.True(value.GetProperty("code_write_failed").GetBoolean());
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task LongRun_StopAndRemove_LeavesNothing()
    {
        const string code = """
            import time

            def site_analysis(df):
                time.sleep(600)
                return {"n": 0}
            """;
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            await runner.StartAsync(Request(taskId, code), CancellationToken.None);
            var running = await WaitForAsync(runner, taskId, CodeRunStatus.Running);
            Assert.NotNull(running.StartedAt);
            Assert.Null(running.FinishedAt);

            await runner.StopAsync(taskId, CancellationToken.None);

            var stopped = await runner.GetAsync(taskId, CancellationToken.None);
            Assert.Equal(CodeRunStatus.Exited, stopped.Status);
            Assert.Equal(running.StartedAt, stopped.StartedAt);
            Assert.NotNull(stopped.FinishedAt);
            var read = await runner.ReadResultAsync(taskId, CodeExecution.MaxResultFileBytes, CancellationToken.None);
            Assert.Null(read.Bytes);
            Assert.False(read.TooLarge);
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task ForgedErrorMessage_NeverReported()
    {
        const string secret = "Zebra7731";
        var csv = Encoding.UTF8.GetBytes($"age,sex\n30,{secret}\n40,F\n");
        const string code = """
            import json
            import os

            def site_analysis(df):
                print(df)
                value = str(df["sex"].iloc[0])
                with open("/outputs/result.json", "w") as f:
                    json.dump({"status": "error", "code": "code_error", "message": value}, f)
                os._exit(0)
            """;
        var taskId = NewTaskId();
        var store = new FakeDatasetStore()
            .Add($"dataset/{Dataset}/cohort.csv", csv)
            .Add($"dataset/{Dataset}/schema.json", CohortSchema);
        var runner = Runner();
        var execution = new CodeExecution(runner, store, new NoOutputs(), _settings, TimeProvider.System);
        var task = new TesTask
        {
            Id = taskId,
            State = TesState.RUNNINGEnum,
            Executors = [new TesExecutor { Image = DockerFactAttribute.RunnerImage, Command = ["sh", "-c", "exit 1"] }],
            Inputs =
            [
                new TesInput { Url = $"dataset://{Dataset}", Path = $"/data/{Dataset}", Type = TesFileType.DIRECTORYEnum },
                new TesInput { Path = "/code/analysis.py", Type = TesFileType.FILEEnum, Content = code }
            ],
            Outputs = [new TesOutput { Path = "/outputs/result.json", Url = "result://", Type = TesFileType.FILEEnum }],
            Resources = new TesResources { CpuCores = 1, RamGb = 2, DiskGb = 0.25 }
        };
        var spec = new FederatedTaskSpec("run-analysis", Dataset, null, [], "study-1", "tre-a",
            Sha(Encoding.UTF8.GetBytes(code)));
        try
        {
            var started = await execution.RunAsync(task, spec, $"dataset/{Dataset}/cohort.csv", CancellationToken.None);
            Assert.True(started.IsInProgress);
            await WaitForAsync(runner, taskId, CodeRunStatus.Exited);

            // The file really holds the CSV value; the report must not.
            var read = await runner.ReadResultAsync(taskId, CodeExecution.MaxResultFileBytes, CancellationToken.None);
            Assert.Contains(secret, Encoding.UTF8.GetString(read.Bytes!));

            var result = await execution.RunAsync(task, spec, null, CancellationToken.None);

            Assert.True(result.IsRunFailure);
            Assert.Equal("code_error", result.ErrorCode);
            Assert.Equal(RunErrorMessages.ForRunnerCode("code_error"), result.ErrorMessage);
            Assert.DoesNotContain(secret, result.ErrorMessage);
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task MissingImage_NotPulled_StartFailsAndLeavesNothing()
    {
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var request = Request(taskId, "def site_analysis(df):\n    return {'n': 0}\n") with
            {
                Image = "python-runner:not-built-" + taskId[..8]
            };

            await Assert.ThrowsAnyAsync<DockerApiException>(() => runner.StartAsync(request, CancellationToken.None));
            Assert.Equal(CodeRunState.None, await runner.GetAsync(taskId, CancellationToken.None));
            await AssertNothingLabelledAsync(taskId); // removed by the failed start itself
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task FileLargerThanDiskLimit_WriteFails()
    {
        // The request's /tmp allowance is 256 MiB; the file size limit is the same on the outputs volume.
        const string code = """
            def site_analysis(df):
                chunk = b"0" * (1024 * 1024)
                try:
                    with open("/outputs/big.bin", "wb") as f:
                        for _ in range(300):
                            f.write(chunk)
                    failed = False
                except OSError:
                    failed = True
                return {"n": 0, "write_failed": failed}
            """;
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var (_, result) = await RunToEndAsync(runner, Request(taskId, code));

            Assert.Equal("ok", result.GetProperty("status").GetString());
            Assert.True(result.GetProperty("result").GetProperty("write_failed").GetBoolean());
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [Theory]
    [InlineData(0L, 256L * MiB, 1.0)]
    [InlineData(5L * MiB, 256L * MiB, 1.0)]
    [InlineData(2048L * MiB, 0L, 1.0)]
    [InlineData(2048L * MiB, 256L * MiB, 0.0)]
    [InlineData(2048L * MiB, 256L * MiB, 1e-12)]
    [InlineData(2048L * MiB, 256L * MiB, double.NaN)]
    public async Task LimitsThatDoNotLimit_Rejected_BeforeDockerIsUsed(long memoryBytes, long tmpBytes, double cpuCores)
    {
        var request = Request(NewTaskId(), "def site_analysis(df):\n    return {'n': 0}\n") with
        {
            MemoryBytes = memoryBytes,
            TmpBytes = tmpBytes,
            CpuCores = cpuCores
        };
        // A client with no daemon behind it: any Docker call would fail with something other than ArgumentException.
        using var unused = new DockerClientConfiguration(new Uri("unix:///nonexistent/docker.sock")).CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new DockerCodeRunner(unused, _settings).StartAsync(request, CancellationToken.None));
    }

    [DockerFact]
    public async Task LargeResultFile_ReadStopsAtLimit()
    {
        const string code = """
            import os

            def site_analysis(df):
                with open("/outputs/result.json", "w") as f:
                    f.write(" " * 70000)
                os._exit(0)
            """;
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            await runner.StartAsync(Request(taskId, code), CancellationToken.None);
            await WaitForAsync(runner, taskId, CodeRunStatus.Exited);

            var read = await runner.ReadResultAsync(taskId, CodeExecution.MaxResultFileBytes, CancellationToken.None);
            Assert.True(read.TooLarge);
            Assert.Null(read.Bytes);
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [DockerFact]
    public async Task NoLogsKept()
    {
        var runner = Runner();
        var taskId = NewTaskId();
        try
        {
            var code = await File.ReadAllTextAsync(Path.Combine(Examples, "analysis.py"));
            var request = Request(taskId, code);
            await runner.StartAsync(request, CancellationToken.None);

            var inspect = await _docker.Containers.InspectContainerAsync($"fed-run-{taskId}");
            var host = inspect.HostConfig;
            Assert.Equal("none", host.LogConfig.Type);

            // The rest of the locked-down container, as the plan fixes it.
            Assert.Equal("none", host.NetworkMode);
            Assert.True(host.ReadonlyRootfs);
            Assert.Equal("1001:0", inspect.Config.User);
            Assert.Equal(["ALL"], host.CapDrop);
            Assert.Equal(["no-new-privileges"], host.SecurityOpt);
            Assert.Equal(256, host.PidsLimit);
            Assert.Equal(request.MemoryBytes, host.Memory);
            Assert.Equal(request.MemoryBytes, host.MemorySwap);
            Assert.Equal(1_000_000_000, host.NanoCPUs);
            Assert.Equal($"rw,noexec,nosuid,size={request.TmpBytes}", host.Tmpfs["/tmp"]);
            var fsize = Assert.Single(host.Ulimits);
            Assert.Equal("fsize", fsize.Name);
            Assert.Equal(request.TmpBytes, fsize.Soft);
            Assert.Equal(request.TmpBytes, fsize.Hard);
            Assert.Equal(
                ["run-analysis", "/code/analysis.py", $"/data/{Dataset}", "/outputs/result.json"],
                inspect.Config.Cmd);
            Assert.Empty(inspect.Config.Env?.Where(e => e.StartsWith("FED", StringComparison.Ordinal)) ?? []);
            Assert.Equal(request.DatasetSha256, inspect.Config.Labels["fed.dataset-sha256"]);
            Assert.Equal("run", inspect.Config.Labels["fed.role"]);

            var mounts = inspect.Mounts.ToDictionary(m => m.Destination);
            Assert.Equal(["/code", "/data", "/outputs"], mounts.Keys.Order().ToArray());
            Assert.Equal($"fed-data-{taskId}", mounts["/data"].Name);
            Assert.False(mounts["/data"].RW);
            Assert.Equal($"fed-code-{taskId}", mounts["/code"].Name);
            Assert.False(mounts["/code"].RW);
            Assert.Equal($"fed-out-{taskId}", mounts["/outputs"].Name);
            Assert.True(mounts["/outputs"].RW);

            await WaitForAsync(runner, taskId, CodeRunStatus.Exited);
        }
        finally
        {
            await RemoveAndAssertNothingLeftAsync(runner, taskId);
        }
    }

    [Theory]
    [InlineData("2026-11-24T12:00:01.123456789Z", "2026-11-24T12:00:01.1234567+00:00")]
    [InlineData("2026-11-24T12:00:01Z", "2026-11-24T12:00:01+00:00")]
    [InlineData("2026-11-24T13:00:01.5+01:00", "2026-11-24T12:00:01.5+00:00")]
    public void ParseDockerTime_ReadsDockerTimes(string text, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), DockerCodeRunner.ParseDockerTime(text));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a time")]
    public void ParseDockerTime_NotSet_Null(string? text)
    {
        Assert.Null(DockerCodeRunner.ParseDockerTime(text));
    }

    private sealed class NoOutputs : IResultOutputStore
    {
        public Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct) => Task.FromResult<byte[]?>(null);

        public Task SaveAsync(string taskId, byte[] content, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkReleasedAsync(string taskId, string resultKey, string resultSha256, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
    }
}
