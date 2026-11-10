using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using Agent.Api.Tests.FederatedTasks;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agent.Api.Tests;

public class DoFederatedTaskWorkTests
{
    private const string Csv = "person_id,age,sex\n1,34,F\n2,51,M\n3,47,F\n";

    private const string T1 = "11111111-1111-1111-1111-111111111111";
    private const string T2 = "22222222-2222-2222-2222-222222222222";

    private sealed class FakeClient : IFederatedTaskClient
    {
        public bool IsConfigured { get; set; } = true;
        public List<TesTask> Pending { get; } = new();
        public List<(FederatedTaskResultReport Report, byte[]? Bytes)> Calls { get; } = new();
        public Queue<FederatedReportOutcome> Outcomes { get; } = new();
        public bool ThrowOnReport { get; set; }
        public int GetPendingCalls { get; private set; }
        public List<(string TaskId, FederatedTaskStep Step)> ProgressCalls { get; } = new();
        public Queue<FederatedReportOutcome> ProgressOutcomes { get; } = new();
        public bool ThrowOnProgress { get; set; }
        public List<string> Order { get; } = new();

        public List<FederatedTaskResultReport> Reports => Calls.Select(c => c.Report).ToList();

        public Task<List<TesTask>> GetPendingAsync()
        {
            GetPendingCalls++;
            return Task.FromResult(Pending);
        }

        public Task<FederatedReportOutcome> ReportProgressAsync(string taskId, FederatedTaskStep step)
        {
            if (ThrowOnProgress) throw new HttpRequestException("network down");
            ProgressCalls.Add((taskId, step));
            Order.Add("progress:" + FederatedTaskSteps.ToWire(step));
            return Task.FromResult(ProgressOutcomes.Count > 0 ? ProgressOutcomes.Dequeue() : new FederatedReportOutcome(200, null, null, null, null));
        }

        public Task<FederatedReportOutcome> ReportAsync(FederatedTaskResultReport report, byte[]? resultFile)
        {
            Order.Add("report:" + report.State);
            if (ThrowOnReport) throw new HttpRequestException("network down");
            Calls.Add((report, resultFile));
            return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : new FederatedReportOutcome(200, null, null, null, null));
        }
    }

    private sealed class FakeResultOutputStore : IResultOutputStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public Dictionary<string, (string Key, string Sha)> Released { get; } = new();
        public bool FailSave { get; set; }
        public bool FailGet { get; set; }
        public bool FailMark { get; set; }
        public int Touches { get; private set; }

        public Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct)
        {
            Touches++;
            if (FailGet) throw new IOException("get failed");
            return Task.FromResult(Files.TryGetValue(taskId, out var b) ? b : null);
        }

        public Task SaveAsync(string taskId, byte[] content, CancellationToken ct)
        {
            Touches++;
            if (FailSave) throw new IOException("save failed");
            Files[taskId] = content;
            return Task.CompletedTask;
        }

        public Task MarkReleasedAsync(string taskId, string resultKey, string resultSha256, CancellationToken ct)
        {
            Touches++;
            if (FailMark) throw new IOException("mark failed");
            Released[taskId] = (resultKey, resultSha256);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Finds any dataset, but throws if its data is read.</summary>
    private sealed class ThrowingReader : IDatasetReader
    {
        public Task<DatasetResolveResult> ResolveAsync(string datasetName, CancellationToken ct) =>
            Task.FromResult(DatasetResolveResult.Ok($"dataset/{datasetName}/data.csv"));

        public Task<DatasetReadResult> ReadAsync(string fileKey, CancellationToken ct) => throw new InvalidOperationException("reader must not be called");
    }

    private sealed class FuncReader(Func<DatasetReadResult> f) : IDatasetReader
    {
        public Task<DatasetResolveResult> ResolveAsync(string datasetName, CancellationToken ct) =>
            Task.FromResult(DatasetResolveResult.Ok($"dataset/{datasetName}/data.csv"));

        public Task<DatasetReadResult> ReadAsync(string fileKey, CancellationToken ct) => Task.FromResult(f());
    }

    private static FakeDatasetStore Store() => new FakeDatasetStore().Add("dataset/cohort_2024/cohort.csv", Csv);

    private static TesTask PendingTask(string id, string dataset, string operation, string? column, params TaskFilter[] filters)
    {
        var task = FederatedTesProfile.Build(new FederatedTaskSpec(operation, dataset, column, filters, "study-1", "tre-a"));
        task.Id = id;
        task.State = TesState.QUEUEDEnum;
        return task;
    }

    private static DoFederatedTaskWork Work(FakeClient client, FakeDatasetStore store, string? treName = "tre-a", FakeResultOutputStore? outputs = null)
        => Work(client, new DatasetReader(store, new FederatedTaskSettings()), treName, outputs);

    private static DoFederatedTaskWork Work(FakeClient client, IDatasetReader reader, string? treName = "tre-a", FakeResultOutputStore? outputs = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TreName"] = treName })
            .Build();
        return new DoFederatedTaskWork(client, reader, outputs ?? new FakeResultOutputStore(), config, new FixedClock());
    }

    [Fact]
    public async Task Execute_ValidMean_ReportsComplete()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "mean", "age", new TaskFilter("sex", "eq", "F")));

        await Work(client, Store()).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(T1, report.TaskId);
        Assert.Equal(TesState.COMPLETEEnum, report.State);
        Assert.Null(report.ErrorCode);
    }

    [Fact]
    public async Task Execute_missing_dataset_reports_rejected_by_input_checks()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "nope", "count", null));

        await Work(client, Store()).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.CANCELEDEnum, report.State);
        Assert.Equal("input_checks", report.RejectedAt);
        Assert.Equal("dataset_not_found", report.ErrorCode);
        // Rejected before it was marked running: the history has no running step.
        Assert.Empty(client.ProgressCalls);
        Assert.False(string.IsNullOrWhiteSpace(report.ErrorMessage));
    }

    [Fact]
    public async Task Execute_MissingColumn_ReportsExecutorErrorColumnNotFound()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "mean", "height"));

        await Work(client, Store()).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.EXECUTORERROREnum, report.State);
        Assert.Equal("column_not_found", report.ErrorCode);
        Assert.Null(report.RejectedAt);
    }

    [Fact]
    public async Task Execute_missing_column_still_reports_executor_error()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "mean", "height"));

        await Work(client, Store()).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.EXECUTORERROREnum, report.State);
        Assert.Null(report.RejectedAt);
    }

    [Fact]
    public async Task Execute_TaskOutsideProfile_ReportsInvalidTask()
    {
        var client = new FakeClient();
        var task = PendingTask(T1, "cohort_2024", "count", null);
        task.Executors[0].Image = "ubuntu:22.04";
        client.Pending.Add(task);

        await Work(client, Store()).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.CANCELEDEnum, report.State);
        Assert.Equal("input_checks", report.RejectedAt);
        Assert.Equal("invalid_task", report.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(report.ErrorMessage));
    }

    [Fact]
    public async Task Execute_OneTaskThrows_OthersStillReported()
    {
        var store = Store().Add("dataset/other/other.csv", "a\n1\n");
        store.ThrowOnOpenKey = "dataset/other/other.csv";
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "other", "count", null));
        client.Pending.Add(PendingTask(T2, "cohort_2024", "count", null));

        await Work(client, store).Execute();

        Assert.Equal(2, client.Reports.Count);
        Assert.Equal(TesState.SYSTEMERROREnum, client.Reports[0].State);
        Assert.Equal("internal_error", client.Reports[0].ErrorCode);
        Assert.DoesNotContain("dataset/other", client.Reports[0].ErrorMessage);
        Assert.Equal(TesState.COMPLETEEnum, client.Reports[1].State);
    }

    [Fact]
    public async Task Execute_NotConfigured_DoesNothing()
    {
        var client = new FakeClient { IsConfigured = false };
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, Store()).Execute();

        Assert.Equal(0, client.GetPendingCalls);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public async Task Execute_TaskForOtherTre_NotRunAndNotReported()
    {
        var store = Store();
        var client = new FakeClient();
        var task = PendingTask(T1, "cohort_2024", "count", null);
        task.Tags!["tres"] = "tre-b";
        client.Pending.Add(task);
        client.Pending.Add(PendingTask(T2, "cohort_2024", "count", null));

        await Work(client, store).Execute();

        // t1 keeps its step on the server; t2 still runs.
        var report = Assert.Single(client.Reports);
        Assert.Equal(T2, report.TaskId);
        Assert.Equal(TesState.COMPLETEEnum, report.State);
        Assert.Equal(1, store.ListCalls);
    }

    [Fact]
    public async Task Execute_TreNameMatchesCaseInsensitively()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, Store(), " TRE-A ").Execute();

        Assert.Equal(TesState.COMPLETEEnum, Assert.Single(client.Reports).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Execute_TreNameNotConfigured_DoesNothing(string? treName)
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, Store(), treName).Execute();

        Assert.Equal(0, client.GetPendingCalls);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public async Task Execute_Complete_DocumentHoldsDatasetSha256()
    {
        var client = new FakeClient();
        var outputs = new FakeResultOutputStore();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, Store(), outputs: outputs).Execute();

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Csv))).ToLowerInvariant();
        Assert.Equal(expected, Doc(outputs.Files[T1]).DatasetSha256);
    }

    [Fact]
    public async Task Execute_ColumnNotFound_ReportsDatasetSha256()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "mean", "height"));

        await Work(client, Store()).Execute();

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Csv))).ToLowerInvariant();
        Assert.Equal(expected, Assert.Single(client.Reports).DatasetSha256);
    }

    [Fact]
    public async Task Execute_DatasetNotFound_ReportsNullDatasetSha256()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "nope", "count", null));

        await Work(client, Store()).Execute();

        Assert.Null(Assert.Single(client.Reports).DatasetSha256);
    }

    private static FederatedResultDocument Doc(byte[] bytes) =>
        System.Text.Json.JsonSerializer.Deserialize<FederatedResultDocument>(bytes, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;

    private static FakeClient OneMean()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "mean", "age", new TaskFilter("sex", "eq", "F")));
        return client;
    }

    [Fact]
    public async Task Success_saves_then_releases_same_bytes()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.COMPLETEEnum, call.Report.State);
        Assert.Equal(outputs.Files[T1], call.Bytes);
        var doc = Doc(call.Bytes!);
        Assert.Equal(40.5m, doc.Result.Value);
        Assert.Equal(2, doc.Result.RowCount);
        Assert.Equal(T1, doc.TaskId);
        Assert.Equal("tre-a", doc.Tre);
        Assert.Equal("study-1", doc.StudyId);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), doc.CompletedAt);
    }

    [Fact]
    public async Task Success_marks_released_when_sha_matches()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var work = Work(client, Store(), outputs: outputs);
        // Run once to learn the sha, then release on a second poll using the saved file.
        await work.Execute();
        var sha = FederatedResultDocumentJson.Sha256Hex(outputs.Files[T1]);
        client.Calls.Clear();
        client.Outcomes.Enqueue(new FederatedReportOutcome(200, null, null, $"1/{T1}/{sha}.json", sha));

        await work.Execute();

        Assert.Equal(($"1/{T1}/{sha}.json", sha), outputs.Released[T1]);
    }

    [Fact]
    public async Task Sha_mismatch_does_not_mark_released()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(200, null, null, "k", new string('a', 64)));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Ok_with_null_key_or_sha_does_not_mark_released()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(200, null, null, null, null));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Save_failure_does_not_report()
    {
        var client = OneMean();
        client.Pending.Add(PendingTask(T2, "cohort_2024", "count", null));
        var outputs = new FakeResultOutputStore { FailSave = true };

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Get_failure_does_not_report()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore { FailGet = true };

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Report_failure_is_isolated_per_task_and_task_stays_queued()
    {
        var client = OneMean();
        client.ThrowOnReport = true;
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Empty(client.Calls);
        Assert.Empty(outputs.Released);
        Assert.True(outputs.Files.ContainsKey(T1));
    }

    [Fact]
    public async Task Retry_reuses_saved_file_and_does_not_recompute()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var saved = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        outputs.Files[T1] = saved;

        await Work(client, new ThrowingReader(), outputs: outputs).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(saved, call.Bytes);
        Assert.Equal(TesState.COMPLETEEnum, call.Report.State);
    }

    [Fact]
    public async Task Storage_unavailable_503_leaves_task_for_retry_and_sends_no_follow_up()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(503, "storage_unavailable", "down", null, null));

        await Work(client, Store()).Execute();

        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData("invalid_result")]
    [InlineData("result_too_large")]
    public async Task Rejected_result_400_sends_follow_up_executor_error(string code)
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(400, code, new string('x', 2000), null, null));

        await Work(client, Store()).Execute();

        Assert.Equal(2, client.Calls.Count);
        var follow = client.Calls[1];
        Assert.Equal(TesState.EXECUTORERROREnum, follow.Report.State);
        Assert.Equal(code, follow.Report.ErrorCode);
        Assert.Null(follow.Bytes);
        Assert.True(follow.Report.ErrorMessage!.Length <= FederatedResultLimits.MaxErrorMessageLength);
        var saved = JsonDocument.Parse(client.Calls[0].Bytes!).RootElement.GetProperty("datasetSha256").GetString();
        Assert.Equal(64, saved!.Length);
        Assert.Equal(saved, follow.Report.DatasetSha256);
    }

    [Fact]
    public async Task Other_400_sends_no_follow_up()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(400, "invalid_task", "bad", null, null));

        await Work(client, Store()).Execute();

        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Conflict_409_with_matching_sha_marks_released()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var work = Work(client, Store(), outputs: outputs);
        await work.Execute();
        var sha = FederatedResultDocumentJson.Sha256Hex(outputs.Files[T1]);
        client.Outcomes.Enqueue(new FederatedReportOutcome(409, "conflict", "already", $"1/{T1}/{sha}.json", sha));

        await work.Execute();

        Assert.Equal(sha, outputs.Released[T1].Sha);
    }

    [Fact]
    public async Task Conflict_409_without_sha_does_not_mark_released_and_sends_nothing_more()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(409, "task_already_reported", "already", null, null));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Single(client.Calls);
        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Conflict_409_with_other_sha_does_not_mark_released()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(409, "conflict", "already", "k", new string('b', 64)));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Error_task_reports_without_file_and_saves_nothing()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "nope", "count", null));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal("dataset_not_found", call.Report.ErrorCode);
        Assert.Null(call.Bytes);
        Assert.Empty(outputs.Files);
    }

    [Fact]
    public async Task Error_message_is_truncated_to_limit()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, new FuncReader(() => DatasetReadResult.Fail("dataset_not_found", new string('m', 2000)))).Execute();

        Assert.True(Assert.Single(client.Calls).Report.ErrorMessage!.Length <= 512);
    }

    [Fact]
    public async Task Internal_error_does_not_leak_exception_text()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, new FuncReader(() => throw new InvalidOperationException("bucket secret-host:9000 unreachable"))).Execute();

        var report = Assert.Single(client.Calls).Report;
        Assert.Equal("internal_error", report.ErrorCode);
        Assert.DoesNotContain("secret-host", report.ErrorMessage);
    }

    [Fact]
    public async Task Overlong_study_id_reports_invalid_task_without_reading_data()
    {
        var client = new FakeClient();
        var task = FederatedTesProfile.Build(new FederatedTaskSpec("count", "cohort_2024", null, Array.Empty<TaskFilter>(), new string('s', 1025), "tre-a"));
        task.Id = T1;
        task.State = TesState.QUEUEDEnum;
        client.Pending.Add(task);
        var outputs = new FakeResultOutputStore();

        await Work(client, new FuncReader(() => throw new InvalidOperationException("dataset must not be read")), outputs: outputs).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.CANCELEDEnum, call.Report.State);
        Assert.Equal("invalid_task", call.Report.ErrorCode);
        Assert.Null(call.Bytes);
        Assert.Empty(outputs.Files);
    }

    [Fact]
    public async Task Non_guid_task_id_reports_invalid_task_without_storage_access()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask("../evil", "cohort_2024", "count", null));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal("invalid_task", call.Report.ErrorCode);
        Assert.Equal(0, outputs.Touches);
    }

    [Fact]
    public async Task Error_message_control_characters_are_replaced()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, new FuncReader(() => DatasetReadResult.Fail("column_not_found", "column 'a\nb\tc' not found"))).Execute();

        var message = Assert.Single(client.Calls).Report.ErrorMessage;
        Assert.Equal("column 'a b c' not found", message);
        Assert.True(FederatedResultLimits.IsValidErrorMessage(message));
    }

    [Fact]
    public async Task Rejected_error_report_does_not_stop_the_loop()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "nope", "count", null));
        client.Pending.Add(PendingTask(T2, "nope", "count", null));
        client.Outcomes.Enqueue(new FederatedReportOutcome(400, "invalid_task", "bad", null, null));

        await Work(client, Store()).Execute();

        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task Execute_queued_task_reports_running_before_result()
    {
        var client = OneMean();

        await Work(client, Store()).Execute();

        var progress = Assert.Single(client.ProgressCalls);
        Assert.Equal((T1, FederatedTaskStep.Running), progress);
        Assert.Equal(new[] { "progress:running", "report:COMPLETEEnum" }, client.Order);
    }

    [Fact]
    public async Task Execute_running_task_is_resumed_without_reporting_running()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;

        await Work(client, Store()).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Equal(TesState.COMPLETEEnum, Assert.Single(client.Reports).State);
    }

    [Fact]
    public async Task Execute_paused_task_is_left_alone()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;

        await Work(client, new ThrowingReader()).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public async Task Execute_running_report_network_failure_leaves_task_for_next_poll()
    {
        var client = OneMean();
        client.ThrowOnProgress = true;

        await Work(client, new ThrowingReader()).Execute();

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Execute_running_report_409_already_final_skips_task()
    {
        var client = OneMean();
        client.ProgressOutcomes.Enqueue(new FederatedReportOutcome(409, "task_already_reported", "final", null, null));

        await Work(client, new ThrowingReader()).Execute();

        Assert.Single(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Execute_task_for_other_tre_does_not_report_running()
    {
        var client = new FakeClient();
        var task = PendingTask(T1, "cohort_2024", "count", null);
        task.Tags!["tres"] = "tre-b";
        client.Pending.Add(task);

        await Work(client, new ThrowingReader()).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(TesState.COMPLETEEnum)]
    [InlineData(TesState.CANCELEDEnum)]
    [InlineData(TesState.EXECUTORERROREnum)]
    [InlineData(TesState.UNKNOWNEnum)]
    public async Task Execute_task_in_unexpected_state_is_skipped(TesState state)
    {
        var client = OneMean();
        client.Pending[0].State = state;

        await Work(client, new ThrowingReader()).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Execute_ambiguous_dataset_is_rejected_without_reporting_running()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));
        var store = Store().Add("dataset/cohort_2024/second.csv", Csv);

        await Work(client, store).Execute();

        Assert.Empty(client.ProgressCalls);
        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.CANCELEDEnum, report.State);
        Assert.Equal("ambiguous_dataset", report.ErrorCode);
    }

    [Fact]
    public async Task Execute_store_failure_while_finding_dataset_leaves_task_for_next_poll()
    {
        var client = OneMean();
        client.Pending.Add(PendingTask(T2, "cohort_2024", "count", null));

        await Work(client, new FailingResolveReader()).Execute();

        // Nothing is reported for either task, and the failure of the first does not stop the second being tried.
        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    private sealed class FailingResolveReader : IDatasetReader
    {
        public Task<DatasetResolveResult> ResolveAsync(string datasetName, CancellationToken ct) => throw new IOException("store down");

        public Task<DatasetReadResult> ReadAsync(string fileKey, CancellationToken ct) => throw new InvalidOperationException("must not be read");
    }

    [Fact]
    public async Task Execute_running_report_409_task_busy_leaves_task_for_next_poll()
    {
        var client = OneMean();
        client.ProgressOutcomes.Enqueue(new FederatedReportOutcome(409, "task_busy", "busy", null, null));

        await Work(client, new ThrowingReader()).Execute();

        Assert.Single(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Result_409_task_busy_does_not_mark_released_and_keeps_the_saved_file()
    {
        var client = OneMean();
        client.Outcomes.Enqueue(new FederatedReportOutcome(409, "task_busy", "busy", null, null));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        // One release attempt, no follow-up report, the TRE copy is kept untagged for the next poll to release.
        Assert.Single(client.Calls);
        Assert.Empty(outputs.Released);
        Assert.Single(outputs.Files);
    }

    [Fact]
    public async Task Error_report_409_task_busy_sends_nothing_more()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "nope", "count", null));
        client.Outcomes.Enqueue(new FederatedReportOutcome(409, "task_busy", "busy", null, null));
        var outputs = new FakeResultOutputStore();

        await Work(client, Store(), outputs: outputs).Execute();

        Assert.Single(client.Calls);
        Assert.Empty(outputs.Files);
    }
}
