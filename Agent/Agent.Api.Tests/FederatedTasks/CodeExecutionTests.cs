using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class CodeExecutionTests
{
    private const string T1 = "6f1c2b9a-4d3e-4f5a-9b8c-7d6e5f4a3b2c";
    private const string T2 = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string T3 = "11111111-2222-4333-8444-555555555555";
    private const string CsvKey = "dataset/cohort_2024/cohort.csv";
    private const string SchemaKey = "dataset/cohort_2024/schema.json";
    private const string Csv = "age,sex\n41,F\n52,M\n";
    private const string Schema = """{"columns":[{"name":"age","type":"integer"},{"name":"sex","type":"string"}]}""";
    private const string Code = "def site_analysis(df):\n    return {'n': len(df)}\n";
    private const string Image = "registry.example/python-runner:1";
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly string CsvSha = Sha(Csv);

    private readonly FakeCodeRunner _runner = new();
    private readonly FakeDatasetStore _store = new FakeDatasetStore().Add(CsvKey, Csv).Add(SchemaKey, Schema);
    private readonly FakeOutputs _outputs = new();
    private readonly FederatedTaskSettings _settings = new();
    private readonly MutableClock _clock = new(Now);

    private CodeExecution Execution() => new(_runner, _store, _outputs, _settings, _clock);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static TesTask CodeTask(TesResources? resources = null) => new()
    {
        Id = T1,
        State = TesState.RUNNINGEnum,
        Executors =
        [
            new TesExecutor
            {
                Image = Image,
                Command = ["run-analysis", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"]
            }
        ],
        Inputs =
        [
            new TesInput { Url = "dataset://cohort_2024", Path = "/data/cohort_2024", Type = TesFileType.DIRECTORYEnum },
            new TesInput { Path = "/code/analysis.py", Type = TesFileType.FILEEnum, Content = Code }
        ],
        Outputs = [new TesOutput { Path = "/outputs/result.json", Url = "result://", Type = TesFileType.FILEEnum }],
        Resources = resources,
        Tags = new Dictionary<string, string>
        {
            ["fed.sites"] = "tre-a",
            ["fed.study_id"] = "study-1",
            ["fed.code_sha256"] = Sha(Code)
        }
    };

    private static readonly FederatedTaskSpec Spec =
        new("run-analysis", "cohort_2024", null, [], "study-1", "tre-a", Sha(Code));

    private void Exited(string? resultJson, bool oom = false, string? stagedSha = null, bool tooLarge = false)
    {
        _runner.States[T1] = new CodeRunState(CodeRunStatus.Exited, Now.AddMinutes(-5), Now.AddMinutes(-4),
            resultJson == null ? 1 : 0, oom, stagedSha ?? CsvSha);
        _runner.Result = tooLarge
            ? new ResultFileRead(null, true)
            : new ResultFileRead(resultJson == null ? null : Encoding.UTF8.GetBytes(resultJson), false);
    }

    private Task<ExecutionResult> Check() => Execution().RunAsync(CodeTask(), Spec, null, CancellationToken.None);

    private static void AssertRunFailed(ExecutionResult result, string code, string message)
    {
        Assert.True(result.IsRunFailure);
        Assert.False(result.IsInProgress);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(message, result.ErrorMessage);
    }

    [Fact]
    public async Task Start_StagesCsvSchemaAndCode_ReturnsInProgress()
    {
        var result = await Execution().RunAsync(CodeTask(), Spec, CsvKey, CancellationToken.None);

        Assert.True(result.IsInProgress);
        var request = Assert.Single(_runner.Started);
        Assert.Equal(T1, request.TaskId);
        Assert.Equal(Image, request.Image);
        Assert.Equal("cohort_2024", request.DatasetName);
        Assert.Equal(CsvSha, request.DatasetSha256);
        Assert.Equal(
            ["data/cohort_2024/cohort.csv", "data/cohort_2024/schema.json", "code/analysis.py"],
            request.Files.Select(f => f.RelativePath).ToArray());
        Assert.Equal(Csv, Encoding.UTF8.GetString(_runner.StagedBytes["data/cohort_2024/cohort.csv"]));
        Assert.Equal(Schema, Encoding.UTF8.GetString(_runner.StagedBytes["data/cohort_2024/schema.json"]));
        Assert.Equal(Code, Encoding.UTF8.GetString(_runner.StagedBytes["code/analysis.py"]));
        // The request has no command: the runner builds it, never from the task's command array.
        Assert.DoesNotContain(typeof(CodeRunRequest).GetProperties(), p => p.Name.Contains("Command"));
    }

    [Fact]
    public async Task Limits_AreDefaultedAndClamped()
    {
        await Execution().RunAsync(CodeTask(), Spec, CsvKey, CancellationToken.None);
        var defaults = _runner.Started[^1];
        Assert.Equal(1, defaults.CpuCores);
        Assert.Equal(8 * GiB, defaults.MemoryBytes);
        Assert.Equal(1 * GiB, defaults.TmpBytes);

        _runner.States.Clear();
        await Execution().RunAsync(CodeTask(new TesResources { CpuCores = 64, RamGb = 512, DiskGb = 100 }), Spec, CsvKey,
            CancellationToken.None);
        var clamped = _runner.Started[^1];
        Assert.Equal(4, clamped.CpuCores);
        Assert.Equal(16 * GiB, clamped.MemoryBytes);
        Assert.Equal(4 * GiB, clamped.TmpBytes);

        _runner.States.Clear();
        await Execution().RunAsync(CodeTask(new TesResources { CpuCores = 2, RamGb = 2.5, DiskGb = 0.5 }), Spec, CsvKey,
            CancellationToken.None);
        var asked = _runner.Started[^1];
        Assert.Equal(2, asked.CpuCores);
        Assert.Equal((long)(2.5 * GiB), asked.MemoryBytes);
        Assert.Equal(GiB / 2, asked.TmpBytes);
    }

    [Fact]
    public async Task Running_WithinLimit_InProgress()
    {
        _runner.States[T1] = new CodeRunState(CodeRunStatus.Running, Now.AddMinutes(-29), null, null, false, CsvSha);

        var result = await Check();

        Assert.True(result.IsInProgress);
        Assert.Empty(_runner.Stopped);
        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task Running_PastLimit_StopsAndTimesOut()
    {
        _runner.States[T1] = new CodeRunState(CodeRunStatus.Running, Now.AddMinutes(-30), null, null, false, CsvSha);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.Timeout, "the code ran longer than the TRE's limit of 30 minutes");
        Assert.Equal([T1], _runner.Stopped);
        // Removed by the poll only once the error report is accepted.
        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task StoppedForTimeout_IsTimeoutOnLaterPoll()
    {
        _runner.States[T1] = new CodeRunState(CodeRunStatus.Exited, Now.AddMinutes(-40), Now.AddMinutes(-10), 137, false, CsvSha);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.Timeout, RunErrorMessages.Timeout(30));
        Assert.Empty(_runner.Reads);
        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task Exited_NoFile_RunFailed()
    {
        Exited(null);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.RunFailed, RunErrorMessages.RunFailed);
        Assert.Equal([(T1, 65_536)], _runner.Reads);
        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task Exited_NoFile_Oom_OutOfMemory()
    {
        Exited(null, oom: true);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.OutOfMemory, RunErrorMessages.OutOfMemory);
    }

    [Fact]
    public async Task Exited_FileTooLarge_ResultTooLarge()
    {
        Exited(null, tooLarge: true);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.ResultTooLarge, RunErrorMessages.InvalidResultFile);
    }

    [Fact]
    public async Task RunnerError_UsesFixedMessage()
    {
        Exited("""{"status":"error","code":"schema_mismatch","message":"column 'age', line 7: SECRET"}""");

        var result = await Check();

        AssertRunFailed(result, "schema_mismatch", "the dataset does not match its schema.json");
        Assert.DoesNotContain("SECRET", result.ErrorMessage);
    }

    [Fact]
    public async Task RunnerError_UnknownCode_InvalidResult()
    {
        Exited("""{"status":"error","code":"made_up","message":"whatever"}""");

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.InvalidResult, RunErrorMessages.InvalidResultFile);
    }

    [Fact]
    public async Task Ok_BuildsAnalysisDocument()
    {
        Exited($$$"""{"status":"ok","datasetSha256":"{{{CsvSha}}}","n":120,"result":{"n":120,"mean_age":41.2}}""");

        var result = await Check();

        Assert.True(result.IsSuccess);
        var doc = result.AnalysisDocument!;
        Assert.Equal(T1, doc.TaskId);
        Assert.Equal("tre-a", doc.Tre);
        Assert.Equal("study-1", doc.StudyId);
        Assert.Equal("run-analysis", doc.Operation);
        Assert.Equal("cohort_2024", doc.Dataset);
        Assert.Equal(CsvSha, doc.DatasetSha256);
        Assert.Equal(Now, doc.CompletedAt);
        Assert.Equal("analysis", doc.Result.Kind);
        Assert.Equal(120, doc.Result.N);
        Assert.Equal(41.2, doc.Result.Value.GetProperty("mean_age").GetDouble());
        Assert.Equal(120, doc.Result.Value.GetProperty("n").GetInt64());
        // Saved and removed by the poll, not here.
        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task Ok_UsesHashFromRunLabel_AfterDatasetReuploaded()
    {
        await Execution().RunAsync(CodeTask(), Spec, CsvKey, CancellationToken.None);
        _store.Add(CsvKey, "age,sex\n99,F\n");
        var staged = _runner.States[T1].DatasetSha256;
        Assert.Equal(CsvSha, staged);
        _runner.States[T1] = _runner.States[T1] with
        {
            Status = CodeRunStatus.Exited, StartedAt = Now.AddMinutes(-2), FinishedAt = Now.AddMinutes(-1), ExitCode = 0
        };
        _runner.Result = new ResultFileRead(Encoding.UTF8.GetBytes(
            $$$"""{"status":"ok","datasetSha256":"{{{CsvSha}}}","n":2,"result":{"n":2}}"""), false);

        var result = await Check();

        Assert.True(result.IsSuccess);
        Assert.Equal(CsvSha, result.AnalysisDocument!.DatasetSha256);
    }

    [Fact]
    public async Task Ok_HashMismatch_InvalidResult()
    {
        Exited($$$"""{"status":"ok","datasetSha256":"{{{Sha("other")}}}","n":2,"result":{"n":2}}""");

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.InvalidResult, RunErrorMessages.InvalidResultFile);
    }

    [Theory]
    [InlineData("""{"status":"ok","datasetSha256":"SHA","n":2,"result":{"n":3}}""")]
    [InlineData("""{"status":"ok","datasetSha256":"SHA","n":2,"result":{"x":1}}""")]
    [InlineData("""{"status":"ok","datasetSha256":"SHA","n":-1,"result":{"n":-1}}""")]
    [InlineData("""{"status":"ok","datasetSha256":"SHA","n":2.5,"result":{"n":2.5}}""")]
    [InlineData("""{"status":"ok","datasetSha256":"SHA","n":"2","result":{"n":"2"}}""")]
    public async Task Ok_NMismatch_InvalidResult(string json)
    {
        Exited(json.Replace("SHA", CsvSha));

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.InvalidResult, RunErrorMessages.InvalidResultFile);
    }

    [Fact]
    public async Task Ok_NestingTooDeep_InvalidResult()
    {
        // result.json is 16 levels deep, which it may be; the document puts the dict one level deeper, at 17.
        var nested = "{}";
        for (var i = 0; i < 13; i++)
        {
            nested = $$"""{"a":{{nested}}}""";
        }

        var json = $$$"""{"status":"ok","datasetSha256":"{{{CsvSha}}}","n":1,"result":{"n":1,"a":{{{nested}}}}}""";
        using (JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }))
        {
        }

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 15 }));
        Exited(json);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.InvalidResult, RunErrorMessages.InvalidResultFile);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"status":"weird"}""")]
    [InlineData("""{"status":"ok","status":"ok"}""")]
    [InlineData("""{"status":"error","code":"schema_mismatch","code":"code_error"}""")]
    [InlineData("""{"status":"error","code":7}""")]
    public async Task Malformed_InvalidResult(string json)
    {
        Exited(json);

        var result = await Check();

        AssertRunFailed(result, FederatedTaskErrorCodes.InvalidResult, RunErrorMessages.InvalidResultFile);
    }

    [Fact]
    public async Task ExitedTwice_SameOutcome()
    {
        // After a lost error report the poll checks the same exited run again.
        Exited("""{"status":"error","code":"code_error","message":"x"}""");

        var first = await Check();
        var second = await Check();

        Assert.Equal(first, second);
        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task StoreFailureWhileStaging_ThrowsDatasetStoreException()
    {
        _store.ThrowOnOpenKey = SchemaKey;

        await Assert.ThrowsAsync<DatasetStoreException>(() =>
            Execution().RunAsync(CodeTask(), Spec, CsvKey, CancellationToken.None));
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public async Task Created_IsRemoved()
    {
        _runner.States[T1] = new CodeRunState(CodeRunStatus.Created, null, null, null, false, CsvSha);

        var result = await Check();

        Assert.True(result.IsInProgress);
        Assert.Equal([T1], _runner.Removed);
    }

    [Fact]
    public async Task HasRun_And_RemoveRun()
    {
        var execution = Execution();
        Assert.False(await execution.HasRunAsync(T1, CancellationToken.None));
        _runner.States[T1] = new CodeRunState(CodeRunStatus.Exited, Now, Now, 0, false, CsvSha);
        Assert.True(await execution.HasRunAsync(T1, CancellationToken.None));

        await execution.RemoveRunAsync(T1, CancellationToken.None);

        Assert.Equal([T1], _runner.Removed);
        Assert.False(await execution.HasRunAsync(T1, CancellationToken.None));
    }

    [Fact]
    public async Task CanStart_FalseAtMaxConcurrentRuns()
    {
        var execution = Execution();
        _runner.States[T2] = new CodeRunState(CodeRunStatus.Running, Now, null, null, false, CsvSha);
        Assert.True(await execution.CanStartAsync(Spec, CancellationToken.None));

        _runner.States[T3] = new CodeRunState(CodeRunStatus.Running, Now, null, null, false, CsvSha);
        Assert.False(await execution.CanStartAsync(Spec, CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_RemovesSavedAndStaleRuns_KeepsPendingOnes()
    {
        const string saved = "22222222-2222-4222-8222-222222222222";
        const string stale = "33333333-3333-4333-8333-333333333333";
        const string staleButPending = "44444444-4444-4444-8444-444444444444";
        const string young = "55555555-5555-4555-8555-555555555555";
        var running = new CodeRunState(CodeRunStatus.Running, Now, null, null, false, CsvSha);
        foreach (var id in new[] { saved, stale, staleButPending, young })
        {
            _runner.States[id] = running;
        }

        _runner.CreatedAt[saved] = Now.AddMinutes(-1);
        _runner.CreatedAt[stale] = Now.AddMinutes(-61);
        _runner.CreatedAt[staleButPending] = Now.AddMinutes(-61);
        _runner.CreatedAt[young] = Now.AddMinutes(-60);
        _outputs.Saved[saved] = [1];

        await Execution().SweepAsync([saved, staleButPending], CancellationToken.None);

        Assert.Equal(new[] { saved, stale }.Order(), _runner.Removed.Order());
    }

    private sealed class FakeOutputs : IResultOutputStore
    {
        public Dictionary<string, byte[]> Saved { get; } = new();

        public Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(Saved.TryGetValue(taskId, out var bytes) ? bytes : null);

        public Task SaveAsync(string taskId, byte[] content, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkReleasedAsync(string taskId, string resultKey, string resultSha256, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
