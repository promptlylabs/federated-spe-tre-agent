using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using Agent.Api.Tests.FederatedTasks;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;
using Xunit;

namespace Agent.Api.Tests;

[Collection(GlobalLoggerCollection.Name)]
public class DoFederatedAggregationWorkTests
{
    private const string Image = "python-runner:1";
    private const string Code = "def site_analysis(df):\n    return {'n': len(df)}\n\n\ndef aggregate(results):\n    return {'n': sum(r['n'] for r in results)}\n";
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeClient _client = new();
    private readonly FakeCodeRunner _runner = new();
    private readonly FakeCodeScanner _scanner = new();
    private readonly FederatedTaskSettings _settings = new() { Role = FederatedTaskSettings.AggregatorRole, ApprovedImages = [Image], MaxRunMinutes = 5 };
    private readonly FixedClock _clock = new();

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset At { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => At;
    }

    private DoFederatedAggregationWork Work() => new(_client, _runner, _scanner, _settings, _clock);

    private sealed class FakeClient : IFederatedTaskClient
    {
        public bool IsConfigured { get; set; } = true;
        public List<FederatedAggregationJob> Jobs { get; } = [];
        public List<(FederatedAggregationReport Report, byte[]? File)> Reports { get; } = [];
        public int Status { get; set; } = 200;

        public Task<List<FederatedAggregationJob>> GetPendingAggregationsAsync() => Task.FromResult(Jobs);

        public Task<FederatedReportOutcome> ReportAggregationAsync(FederatedAggregationReport report, byte[]? pooledFile)
        {
            Reports.Add((report, pooledFile));
            return Task.FromResult(new FederatedReportOutcome(Status, null, null, null, null));
        }

        public Task<List<TesTask>> GetPendingAsync() => throw new NotSupportedException();
        public Task<FederatedReportOutcome> ReportAsync(FederatedTaskResultReport report, byte[]? resultFile) => throw new NotSupportedException();
        public Task<FederatedReportOutcome> ReportProgressAsync(string taskId, FederatedTaskPhase phase) => throw new NotSupportedException();
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private FederatedAggregationJob Job(string code = Code, string? codeSha = null, string? tagSha = null, string image = Image,
        string results = "[{\"n\":30,\"mean_age\":40.0},{\"n\":10,\"mean_age\":20.0}]", string evidence = "[null,null]")
    {
        var task = FederatedTesProfile.BuildCodeTask("cohort_2024", code, "study-1", "tre-a|tre-b");
        task.Executors![0].Image = image;
        task.Tags![FederatedTesProfile.CodeSha256Tag] = tagSha ?? Sha(code);
        var job = new FederatedAggregationJob
        {
            VersionId = Guid.NewGuid(), StudyId = "study-1", Version = 1, TesTaskJson = JsonConvert.SerializeObject(task),
            CodeSha256 = codeSha ?? Sha(code), ResultsJson = results, EvidenceJson = evidence
        };
        _client.Jobs.Add(job);
        return job;
    }

    private static string Id(FederatedAggregationJob job) => job.VersionId.ToString("D");

    private void Exited(FederatedAggregationJob job, string? resultJson, bool oom = false, bool tooLarge = false, int minutes = 1)
    {
        _runner.States[Id(job)] = new CodeRunState(CodeRunStatus.Exited, Now.AddMinutes(-minutes), Now, 0, oom, null);
        _runner.Result = new ResultFileRead(resultJson is null ? null : Encoding.UTF8.GetBytes(resultJson), tooLarge);
    }

    private (string Phase, string? Code) Last() => (_client.Reports[^1].Report.Phase, _client.Reports[^1].Report.ErrorCode);

    [Theory]
    [InlineData("code", "invalid_task")]
    [InlineData("tag", "invalid_task")]
    [InlineData("image", "image_not_approved")]
    [InlineData("scan", "unsafe_code")]
    [InlineData("scanner", "internal_error")]
    [InlineData("small", "small_numbers")]   // pooled n 3 + 1 below the minimum of 10: aggregate() never runs
    public async Task Refuses_before_staging_anything(string what, string code)
    {
        _scanner.Result = what switch
        {
            "scan" => new CodeScanResult(CodeScanKind.Findings, [new CodeScanFinding("import-not-allowed", 1, "m")], "test", FakeCodeScanner.Sha, null),
            "scanner" => new CodeScanResult(CodeScanKind.ScannerFailed, [], "test", FakeCodeScanner.Sha, "boom"),
            _ => _scanner.Result
        };
        var job = what switch
        {
            "code" => Job(codeSha: new string('0', 64)),
            "tag" => Job(tagSha: new string('0', 64)),
            "image" => Job(image: "python:3.12"),
            "small" => Job(results: "[{\"n\":3},{\"n\":1}]"),
            _ => Job()
        };

        await Work().Execute();

        Assert.Empty(_runner.Started);
        Assert.Equal((FederatedAggregationPhases.Failed, code), Last());
        Assert.Equal(job.VersionId.ToString(), _client.Reports[^1].Report.VersionId);
    }

    [Fact]
    public async Task Starts_a_new_job_with_the_code_and_the_results_only()
    {
        var job = Job();

        await Work().Execute();

        Assert.Equal((FederatedAggregationPhases.Running, (string?)null), Last());
        var request = Assert.Single(_runner.Started);
        Assert.Equal(Id(job), request.TaskId);
        Assert.Equal(Image, request.Image);
        Assert.Equal(new[] { "run-analysis", "--aggregate", "/code/analysis.py", "/data/results.json", "/outputs/result.json" }, request.Command);
        Assert.Equal(new[] { "code/analysis.py", "data/results.json" }, _runner.StagedBytes.Keys.Order());
        Assert.Equal(Code, Encoding.UTF8.GetString(_runner.StagedBytes["code/analysis.py"]));
        Assert.Equal(job.ResultsJson, Encoding.UTF8.GetString(_runner.StagedBytes["data/results.json"]));
    }

    [Fact]
    public async Task Waits_while_the_scan_rules_are_unavailable()
    {
        _scanner.Result = new CodeScanResult(CodeScanKind.RulesUnavailable, [], null, null, "no rule file");
        Job();

        await Work().Execute();

        Assert.Empty(_runner.Started);
        Assert.Empty(_client.Reports);
    }

    [Fact]
    public async Task Waits_while_the_limit_on_runs_is_reached()
    {
        _settings.MaxConcurrentRuns = 1;
        var running = Job();
        _runner.States[Id(running)] = new CodeRunState(CodeRunStatus.Running, Now, null, null, false, null);
        Job();

        await Work().Execute();

        Assert.Empty(_runner.Started);
        Assert.Empty(_client.Reports);
    }

    [Fact]
    public async Task Reports_the_pooled_document_and_then_removes_the_run()
    {
        var job = Job();
        Exited(job, "{\"status\":\"ok\",\"n\":4,\"result\":{\"n\":4,\"mean_age\":35.0}}");

        await Work().Execute();

        Assert.Equal((FederatedAggregationPhases.Aggregated, (string?)null), Last());
        var doc = JsonDocument.Parse(_client.Reports[^1].File!).RootElement;
        Assert.Equal(job.VersionId, doc.GetProperty("versionId").GetGuid());
        Assert.Equal(("study-1", 1, job.CodeSha256, 2), (doc.GetProperty("studyId").GetString(), doc.GetProperty("version").GetInt32(),
            doc.GetProperty("codeSha256").GetString(), doc.GetProperty("sites").GetInt32()));
        Assert.Equal("aggregate", doc.GetProperty("result").GetProperty("kind").GetString());
        Assert.Equal(35.0, doc.GetProperty("result").GetProperty("value").GetProperty("mean_age").GetDouble());
        Assert.Equal("[]", doc.GetProperty("result").GetProperty("tables").GetRawText());
        Assert.Equal("""{"minimum":10,"withheldCells":0,"valueChecked":false}""", doc.GetProperty("result").GetProperty("checks").GetRawText());
        Assert.Equal([Id(job)], _runner.Removed);
    }

    [Fact]
    public async Task Pools_the_acro_tables_into_the_document()
    {
        const string table = "{\"method\":\"crosstab\",\"statistic\":\"count\",\"rows\":[\"F\",\"M\"],\"columns\":[\"x\"],\"count\":[[9],[3]]}";
        var job = Job(evidence: $"[{{\"tables\":[{table}]}},{{\"tables\":[{table.Replace("[[9],[3]]", "[[2],[1]]")}]}}]");
        Exited(job, "{\"status\":\"ok\",\"n\":4,\"result\":{\"n\":4}}");

        await Work().Execute();

        var result = JsonDocument.Parse(_client.Reports[^1].File!).RootElement.GetProperty("result");
        Assert.Equal("[[11],[null]]", result.GetProperty("tables")[0].GetProperty("cells").GetRawText());
        Assert.Equal(1, result.GetProperty("checks").GetProperty("withheldCells").GetInt32());
    }

    [Theory]
    [InlineData("mismatch", "invalid_result")]
    [InlineData("too large", "result_too_large")]
    public async Task Pooling_problems_fail_the_study(string what, string code)
    {
        var job = what == "mismatch"
            ? Job(evidence: "[{\"tables\":[{\"method\":\"crosstab\",\"statistic\":\"count\",\"rows\":[\"F\"],\"columns\":[\"x\"],\"count\":[[20]]}]},null]")
            : Job();
        // aggregate()'s 65,400 bytes fit its own run's limit but not the pooled document's.
        Exited(job, what == "mismatch" ? "{\"status\":\"ok\",\"n\":4,\"result\":{\"n\":4}}"
            : $"{{\"status\":\"ok\",\"n\":4,\"result\":{{\"n\":4,\"pad\":\"{new string('x', 65400)}\"}}}}");

        await Work().Execute();

        Assert.Equal((FederatedAggregationPhases.Failed, code), Last());
        Assert.Null(_client.Reports[^1].File);
    }

    [Fact]
    public async Task Keeps_the_run_when_the_report_is_not_accepted()
    {
        var job = Job();
        Exited(job, "{\"status\":\"ok\",\"n\":4,\"result\":{\"n\":4}}");
        _client.Status = 503;

        await Work().Execute();
        _clock.At = Now.AddMinutes(1);
        await Work().Execute();

        Assert.Empty(_runner.Removed);
        // The same bytes each time, so a retried report stores the same file under the same key.
        Assert.Equal(_client.Reports[0].File, _client.Reports[1].File);
    }

    [Theory]
    [InlineData("{\"status\":\"error\",\"code\":\"code_error\",\"message\":\"aggregate raised ValueError at analysis.py line 2\"}", false, false, "code_error")]
    [InlineData("{\"status\":\"error\",\"code\":\"invalid_result\",\"message\":\"m\"}", false, false, "invalid_result")]
    [InlineData("{\"status\":\"error\",\"code\":\"invalid_dataset\",\"message\":\"m\"}", false, false, "internal_error")]
    [InlineData("{\"status\":\"error\",\"code\":\"made_up\",\"message\":\"m\"}", false, false, "invalid_result")]
    [InlineData("not json", false, false, "invalid_result")]
    [InlineData("{\"status\":\"ok\",\"n\":4,\"result\":{\"n\":5}}", false, false, "invalid_result")]
    [InlineData(null, true, false, "out_of_memory")]
    [InlineData(null, false, false, "run_failed")]
    [InlineData(null, false, true, "result_too_large")]
    public async Task Maps_a_failed_run(string? resultJson, bool oom, bool tooLarge, string code)
    {
        var job = Job();
        Exited(job, resultJson, oom, tooLarge);

        await Work().Execute();

        Assert.Equal((FederatedAggregationPhases.Failed, code), Last());
        Assert.Null(_client.Reports[^1].File);
        Assert.Equal([Id(job)], _runner.Removed);
    }

    [Theory]
    [InlineData("aggregate raised ValueError at analysis.py line 2", 2)]
    [InlineData("importing analysis.py raised SyntaxError at line 5", 5)]
    [InlineData("analysis.py has no aggregate function", null)]
    public async Task Code_error_reports_only_the_line_number(string runnerMessage, int? line)
    {
        var job = Job();
        Exited(job, System.Text.Json.JsonSerializer.Serialize(new { status = "error", code = "code_error", message = runnerMessage }));

        await Work().Execute();

        Assert.Equal((FederatedAggregationPhases.Failed, "code_error", line),
            (_client.Reports[^1].Report.Phase, _client.Reports[^1].Report.ErrorCode, _client.Reports[^1].Report.ErrorLine));
    }

    [Fact]
    public async Task Stops_a_run_past_the_time_limit()
    {
        var job = Job();
        _runner.States[Id(job)] = new CodeRunState(CodeRunStatus.Running, Now.AddMinutes(-5), null, null, false, null);

        await Work().Execute();

        Assert.Equal([Id(job)], _runner.Stopped);
        Assert.Equal((FederatedAggregationPhases.Failed, "timeout"), Last());
    }

    [Fact]
    public async Task Leaves_a_run_that_is_still_going()
    {
        var job = Job();
        _runner.States[Id(job)] = new CodeRunState(CodeRunStatus.Running, Now.AddMinutes(-1), null, null, false, null);

        await Work().Execute();

        Assert.Empty(_client.Reports);
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public async Task Removes_runs_and_staged_data_of_versions_no_longer_pending()
    {
        var job = Job();
        _runner.States[Id(job)] = new CodeRunState(CodeRunStatus.Running, Now.AddMinutes(-1), null, null, false, null);
        _runner.States["old"] = new CodeRunState(CodeRunStatus.Exited, Now.AddMinutes(-9), Now, 0, false, null);
        _runner.Orphans.Add("orphan");

        await Work().Execute();

        Assert.Equal(new[] { "old", "orphan" }, _runner.Removed.Order());
    }

    /// <summary>
    /// Submission leaves a version out when its storage is briefly down; its run, finished or not, must survive that poll,
    /// as a hospital Agent keeps a run that isn't pending until it is twice MaxRunMinutes old.
    /// </summary>
    [Fact]
    public async Task Keeps_a_recent_run_whose_version_was_left_out_of_this_poll()
    {
        _runner.States["skipped"] = new CodeRunState(CodeRunStatus.Exited, Now.AddMinutes(-2), Now, 0, false, null);
        _runner.CreatedAt["skipped"] = Now.AddMinutes(-2);

        await Work().Execute();

        Assert.Empty(_runner.Removed);
    }

    [Fact]
    public async Task Does_nothing_without_credentials()
    {
        _client.IsConfigured = false;
        Job();

        await Work().Execute();

        Assert.Empty(_runner.Started);
        Assert.Empty(_client.Reports);
    }
}
