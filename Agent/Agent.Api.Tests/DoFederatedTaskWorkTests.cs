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

    /// <summary>Output approval stand-in. By default it approves every registration at once.</summary>
    private sealed class FakeOutputApproval : IOutputApproval
    {
        public bool AutoApprove { get; set; } = true;
        public bool ThrowOnRegister { get; set; }
        public Dictionary<string, OutputReview> Records { get; } = new();
        public Dictionary<string, EgressRegistration> Registered { get; } = new();
        /// <summary>Task ids with a registration marker (the Egress app has accepted the registration).</summary>
        public HashSet<string> Marked { get; } = new();
        public int RegisterCalls { get; private set; }

        public FakeOutputApproval Preset(string taskId, OutputReview review, bool registered = false)
        {
            Records[taskId] = review;
            if (registered) Marked.Add(taskId);
            return this;
        }

        public Task<OutputReview?> GetAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(Records.TryGetValue(taskId, out var r) ? r : null);

        public Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(Marked.Contains(taskId));

        public Task<OutputReview> RegisterAsync(EgressRegistration r, CancellationToken ct)
        {
            RegisterCalls++;
            if (ThrowOnRegister) throw new HttpRequestException("egress down");
            Registered[r.TaskId] = r;
            if (Records.TryGetValue(r.TaskId, out var existing) && !existing.IsPending) return Task.FromResult(existing);

            var record = AutoApprove
                ? Approved(r.ResultSha256)
                : existing ?? OutputReview.Pending(r.ResultSha256, Now);
            Records[r.TaskId] = record;
            Marked.Add(r.TaskId);
            return Task.FromResult(record);
        }

        public Task<DecisionOutcome> RecordDecisionAsync(string taskId, ReviewDecision d, CancellationToken ct) =>
            throw new NotSupportedException();

        public HashSet<string> Withdrawn { get; } = new();

        public Task<bool> IsWithdrawnAsync(string taskId, CancellationToken ct) => Task.FromResult(Withdrawn.Contains(taskId));

        public Task WithdrawAsync(string taskId, string reason, CancellationToken ct)
        {
            Withdrawn.Add(taskId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEgressClient : IEgressReviewClient
    {
        public bool IsConfigured { get; set; } = true;

        public Task RegisterAsync(EgressRegistration r, CancellationToken ct) => throw new NotSupportedException();

        public Task WithdrawAsync(string taskId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    private static OutputReview PendingReview(string sha) => OutputReview.Pending(sha, Now);

    private static OutputReview Approved(string sha) =>
        new(sha, Now, DisclosureCheckStatus.Completed, "reviewer", Now);

    private static OutputReview Rejected(string sha, string reason) =>
        new(sha, Now, DisclosureCheckStatus.Failed, "reviewer", Now, reason);

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
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
        // Submission always sets it; a minute before FixedClock makes the task young enough to be retried.
        task.CreationTime = Now.AddMinutes(-1);
        return task;
    }

    private static DoFederatedTaskWork Work(FakeClient client, FakeDatasetStore store, string? treName = "tre-a", FakeResultOutputStore? outputs = null)
        => Work(client, new DatasetReader(store, new FederatedTaskSettings()), treName, outputs);

    private static DoFederatedTaskWork Work(FakeClient client, IDatasetReader reader, string? treName = "tre-a",
        FakeResultOutputStore? outputs = null, FederatedTaskSettings? settings = null, DateTimeOffset? startedAt = null,
        IFederatedTaskExecution? execution = null, FakeOutputApproval? approval = null, FakeEgressClient? egress = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TreName"] = treName })
            .Build();
        var clock = new FixedClock();
        // An Agent that started long ago, so the give-up window counts from when the task was queued.
        return new DoFederatedTaskWork(client, reader, execution ?? new OperationExecution(reader, clock),
            outputs ?? new FakeResultOutputStore(),
            approval ?? new FakeOutputApproval(), egress ?? new FakeEgressClient(), config, clock,
            settings ?? Approving(FederatedTesProfile.ExecutorImage), new AgentStartTime(startedAt ?? Now.AddDays(-1)));
    }

    private static FederatedTaskSettings Approving(params string[] images) => new() { ApprovedImages = images };

    private static DoFederatedTaskWork Work(FakeClient client, FakeDatasetStore store, FederatedTaskSettings settings,
        FakeResultOutputStore? outputs = null)
        => Work(client, new DatasetReader(store, new FederatedTaskSettings()), outputs: outputs, settings: settings);

    [Fact]
    public async Task Execute_unapproved_image_is_rejected_by_input_checks()
    {
        var store = Store();
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, store, Approving("other:1")).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.CANCELEDEnum, report.State);
        Assert.Equal("input_checks", report.RejectedAt);
        Assert.Equal("image_not_approved", report.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(report.ErrorMessage));
        Assert.Empty(client.ProgressCalls);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Execute_running_task_whose_image_is_no_longer_approved_is_rejected()
    {
        var client = new FakeClient();
        var task = PendingTask(T1, "cohort_2024", "count", null);
        task.State = TesState.RUNNINGEnum;
        client.Pending.Add(task);

        await Work(client, Store(), Approving("other:1")).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.CANCELEDEnum, report.State);
        Assert.Equal("image_not_approved", report.ErrorCode);
        Assert.Empty(client.ProgressCalls);
    }

    [Fact]
    public async Task Execute_without_approved_images_holds_tasks_and_reports_nothing()
    {
        var store = Store();
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

        await Work(client, store, Approving()).Execute();

        Assert.Empty(client.Reports);
        Assert.Empty(client.ProgressCalls);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task Execute_without_approved_images_still_releases_a_saved_result()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));
        var outputs = new FakeResultOutputStore();
        outputs.Files[T1] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");

        await Work(client, new ThrowingReader(), outputs: outputs, settings: Approving()).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.COMPLETEEnum, report.State);
    }

    [Fact]
    public async Task Execute_task_outside_profile_is_rejected_as_invalid_task_before_the_image_check()
    {
        var client = new FakeClient();
        var task = PendingTask(T1, "cohort_2024", "count", null);
        task.Executors[0].Image = "ubuntu:22.04";
        client.Pending.Add(task);

        await Work(client, Store(), Approving("other:1")).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal("invalid_task", report.ErrorCode);
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
        var failing = PendingTask(T1, "other", "count", null);
        failing.CreationTime = Now.AddMinutes(-60); // old enough that the read failure is reported, not retried
        client.Pending.Add(failing);
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

    private static IDatasetReader Reader() => new DatasetReader(Store(), new FederatedTaskSettings());

    private static FakeClient OneAgeMean()
    {
        var client = new FakeClient();
        client.Pending.Add(PendingTask(T1, "cohort_2024", "mean", "age"));
        return client;
    }

    private static FakeResultOutputStore Saved(string content = "{\"schemaVersion\":1}")
    {
        var outputs = new FakeResultOutputStore();
        outputs.Files[T1] = Encoding.UTF8.GetBytes(content);
        return outputs;
    }

    private static string ShaOf(byte[] bytes) => FederatedResultDocumentJson.Sha256Hex(bytes);

    [Fact]
    public async Task Computed_result_is_registered_and_reported_waiting_not_uploaded()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false };

        await Work(client, Reader(), outputs: outputs, approval: approval).Execute();

        Assert.Equal((T1, FederatedTaskStep.WaitingForOutputApproval), client.ProgressCalls[^1]);
        Assert.DoesNotContain(client.Reports, r => r.State == TesState.COMPLETEEnum);
        Assert.Empty(client.Calls);
        Assert.Equal(ShaOf(outputs.Files[T1]), approval.Registered[T1].ResultSha256);
    }

    [Fact]
    public async Task Registration_carries_study_code_and_result()
    {
        var client = OneAgeMean();
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false };

        await Work(client, Reader(), outputs: outputs, approval: approval).Execute();

        var r = approval.Registered[T1];
        Assert.Equal(T1, r.TaskId);
        Assert.Equal("study-1", r.StudyId);
        Assert.Equal("5s-tes/federated-ops:1 mean --column age", r.Code);
        Assert.Equal($"Federated task {T1}: mean --column age", r.Name);
        Assert.Equal(Encoding.UTF8.GetString(outputs.Files[T1]), r.ResultContent);
        Assert.Equal(new FederatedTaskSettings().OutputBucket, r.OutputBucket);
    }

    [Fact]
    public async Task Paused_task_without_decision_reports_nothing()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, PendingReview(ShaOf(outputs.Files[T1])), registered: true);

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Calls);
        Assert.Equal(0, approval.RegisterCalls);
    }

    [Fact]
    public async Task Paused_task_without_file_or_record_is_skipped_without_computing()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false };

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Calls);
        Assert.Empty(outputs.Files);
        Assert.Equal(0, approval.RegisterCalls);
    }

    [Fact]
    public async Task Paused_approved_task_is_released_with_the_reviewed_sha()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var outputs = Saved();
        var sha = ShaOf(outputs.Files[T1]);
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Approved(sha));
        client.Outcomes.Enqueue(new FederatedReportOutcome(200, null, null, $"1/{T1}/{sha}.json", sha));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.COMPLETEEnum, call.Report.State);
        Assert.Equal(sha, ShaOf(call.Bytes!));
        Assert.Equal(($"1/{T1}/{sha}.json", sha), outputs.Released[T1]);
        Assert.Empty(client.ProgressCalls);
        // A decided record means the Egress app has the row, even without a registration marker.
        Assert.Equal(0, approval.RegisterCalls);
    }

    [Fact]
    public async Task Approved_task_whose_file_changed_is_failed_and_not_uploaded()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Approved(ShaOf(outputs.Files[T1])));
        outputs.Files[T1] = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.SYSTEMERROREnum, call.Report.State);
        Assert.Equal("result_changed_after_review", call.Report.ErrorCode);
        Assert.Equal("the saved result no longer matches the reviewed result", call.Report.ErrorMessage);
        Assert.Null(call.Report.RejectedAt);
        Assert.Null(call.Bytes);
        Assert.Empty(outputs.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_task_whose_file_changed_since_the_review_record_is_failed_not_registered_or_waiting(bool registered)
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, PendingReview(new string('d', 64)), registered);

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.SYSTEMERROREnum, call.Report.State);
        Assert.Equal("result_changed_after_review", call.Report.ErrorCode);
        Assert.Equal("the saved result no longer matches the reviewed result", call.Report.ErrorMessage);
        Assert.Null(call.Bytes);
        Assert.Equal(0, approval.RegisterCalls);
        Assert.Empty(client.ProgressCalls);
        Assert.Empty(outputs.Released);
        Assert.Contains(T1, approval.Withdrawn);
    }

    [Fact]
    public async Task Pending_task_whose_file_is_gone_is_withdrawn_and_failed()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, PendingReview(new string('c', 64)), true);

        await Work(client, new ThrowingReader(), approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal("result_changed_after_review", call.Report.ErrorCode);
        Assert.Contains(T1, approval.Withdrawn);
    }

    [Fact]
    public async Task Approved_task_whose_file_is_gone_is_failed_without_recomputing()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Approved(new string('c', 64)));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.SYSTEMERROREnum, call.Report.State);
        Assert.Equal("result_changed_after_review", call.Report.ErrorCode);
        Assert.Null(call.Bytes);
        Assert.Empty(outputs.Files);
        Assert.Equal(0, approval.RegisterCalls);
        // Decided reviews are not withdrawn: the decision already reached the Agent.
        Assert.Empty(approval.Withdrawn);
    }

    [Fact]
    public async Task Rejected_task_reports_rejection_at_output_approval_with_reason()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Rejected(ShaOf(outputs.Files[T1]), "small cells"));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.CANCELEDEnum, call.Report.State);
        Assert.Equal("output_approval", call.Report.RejectedAt);
        Assert.Equal("rejected_by_reviewer", call.Report.ErrorCode);
        Assert.Equal("small cells", call.Report.ErrorMessage);
        Assert.Null(call.Bytes);
        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Rejected_task_whose_file_is_gone_still_reports_the_rejection()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Rejected(new string('c', 64), "small cells"));

        await Work(client, new ThrowingReader(), approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.CANCELEDEnum, call.Report.State);
        Assert.Equal("rejected_by_reviewer", call.Report.ErrorCode);
        Assert.Null(call.Bytes);
    }

    [Fact]
    public async Task ActOnDecision_releases_an_approved_task_that_is_not_in_the_pending_list()
    {
        var client = OneMean();
        client.Pending.Clear();
        var outputs = Saved();
        var sha = ShaOf(outputs.Files[T1]);
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Approved(sha));
        client.Outcomes.Enqueue(new FederatedReportOutcome(200, null, null, $"1/{T1}/{sha}.json", sha));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).ActOnDecision(T1.ToUpperInvariant());

        var call = Assert.Single(client.Calls);
        Assert.Equal(T1, call.Report.TaskId);
        Assert.Equal(TesState.COMPLETEEnum, call.Report.State);
        Assert.Equal(sha, ShaOf(call.Bytes!));
        Assert.Equal(($"1/{T1}/{sha}.json", sha), outputs.Released[T1]);
    }

    [Fact]
    public async Task ActOnDecision_reports_a_rejection_with_its_reason()
    {
        var client = OneMean();
        client.Pending.Clear();
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Rejected(ShaOf(outputs.Files[T1]), "small cells"));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).ActOnDecision(T1);

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.CANCELEDEnum, call.Report.State);
        Assert.Equal("rejected_by_reviewer", call.Report.ErrorCode);
        Assert.Equal("small cells", call.Report.ErrorMessage);
        Assert.Null(call.Bytes);
    }

    [Fact]
    public async Task ActOnDecision_fails_an_approved_task_whose_file_changed()
    {
        var client = OneMean();
        client.Pending.Clear();
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Approved(ShaOf(outputs.Files[T1])));
        outputs.Files[T1] = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).ActOnDecision(T1);

        var call = Assert.Single(client.Calls);
        Assert.Equal("result_changed_after_review", call.Report.ErrorCode);
        Assert.Null(call.Bytes);
        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task ActOnDecision_does_nothing_for_a_pending_or_unknown_review()
    {
        var client = OneMean();
        client.Pending.Clear();
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, PendingReview(ShaOf(outputs.Files[T1])), true);

        var work = Work(client, new ThrowingReader(), outputs: outputs, approval: approval);
        await work.ActOnDecision(T1);
        await work.ActOnDecision("22222222-2222-2222-2222-222222222222");
        await work.ActOnDecision("not-a-guid");

        Assert.Empty(client.Calls);
        Assert.Empty(client.ProgressCalls);
        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Rejection_reason_is_cleaned_and_truncated()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.PAUSEDEnum;
        var outputs = Saved();
        var reason = "small\ncells\t" + new string('r', 2000);
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, Rejected(ShaOf(outputs.Files[T1]), reason));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var message = Assert.Single(client.Calls).Report.ErrorMessage!;
        Assert.Equal(FederatedResultLimits.MaxErrorMessageLength, message.Length);
        Assert.StartsWith("small cells r", message);
        Assert.True(FederatedResultLimits.IsValidErrorMessage(message));
    }

    [Fact]
    public async Task Running_task_with_unregistered_record_registers_again()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = Saved();
        var sha = ShaOf(outputs.Files[T1]);
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, PendingReview(sha), registered: false);

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        Assert.Equal(sha, approval.Registered[T1].ResultSha256);
        Assert.Contains(T1, approval.Marked);
        Assert.Equal((T1, FederatedTaskStep.WaitingForOutputApproval), Assert.Single(client.ProgressCalls));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Running_task_with_registered_pending_record_reports_waiting_without_registering()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }.Preset(T1, PendingReview(ShaOf(outputs.Files[T1])), registered: true);

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        Assert.Equal(0, approval.RegisterCalls);
        Assert.Equal((T1, FederatedTaskStep.WaitingForOutputApproval), Assert.Single(client.ProgressCalls));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Egress_not_configured_holds_the_result_and_uploads_nothing()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false };

        await Work(client, Reader(), outputs: outputs, approval: approval, egress: new FakeEgressClient { IsConfigured = false }).Execute();

        Assert.Empty(client.Calls);
        Assert.DoesNotContain(client.ProgressCalls, p => p.Step == FederatedTaskStep.WaitingForOutputApproval);
        Assert.True(outputs.Files.ContainsKey(T1));
        Assert.Equal(0, approval.RegisterCalls);
    }

    [Fact]
    public async Task Registration_failure_leaves_task_running_for_next_poll()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false, ThrowOnRegister = true };

        await Work(client, Reader(), outputs: outputs, approval: approval).Execute();

        Assert.Equal((T1, FederatedTaskStep.Running), Assert.Single(client.ProgressCalls));
        Assert.Empty(client.Calls);
        Assert.True(outputs.Files.ContainsKey(T1));
    }

    [Theory]
    [InlineData(23 * 60, false)]   // first sent for review 23 hours ago: still an outage
    [InlineData(25 * 60, true)]    // 25 hours ago: past the one-day default
    public async Task Registration_failing_since_the_result_was_first_sent_gives_up_after_the_limit(int minutesAgo, bool givesUp)
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false, ThrowOnRegister = true }
            .Preset(T1, OutputReview.Pending(ShaOf(outputs.Files[T1]), Now.AddMinutes(-minutesAgo)));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval, startedAt: Now.AddDays(-3)).Execute();

        if (givesUp)
        {
            var call = Assert.Single(client.Calls);
            Assert.Equal(TesState.SYSTEMERROREnum, call.Report.State);
            Assert.Equal("internal_error", call.Report.ErrorCode);
            Assert.Null(call.Bytes);
            Assert.Contains(T1, approval.Withdrawn);
        }
        else
        {
            Assert.Empty(client.Calls);
            Assert.Empty(approval.Withdrawn);
        }
        Assert.Empty(client.ProgressCalls);
        Assert.Empty(outputs.Released);
    }

    [Fact]
    public async Task Withdrawn_pending_review_is_reported_failed_and_not_registered_again()
    {
        // Registration gave up and withdrew the review, but the failure report did not get through.
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false }
            .Preset(T1, OutputReview.Pending(ShaOf(outputs.Files[T1]), Now.AddDays(-2)));
        approval.Withdrawn.Add(T1);

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.SYSTEMERROREnum, call.Report.State);
        Assert.Equal("internal_error", call.Report.ErrorCode);
        Assert.Equal(0, approval.RegisterCalls);
        Assert.Empty(client.ProgressCalls);
    }

    [Fact]
    public async Task Registration_failure_limit_counts_from_agent_start_when_that_is_later()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        var outputs = Saved();
        var approval = new FakeOutputApproval { AutoApprove = false, ThrowOnRegister = true }
            .Preset(T1, OutputReview.Pending(ShaOf(outputs.Files[T1]), Now.AddDays(-3)));

        await Work(client, new ThrowingReader(), outputs: outputs, approval: approval, startedAt: Now.AddHours(-1)).Execute();

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Registration_failure_with_a_zero_limit_gives_up_at_once()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var approval = new FakeOutputApproval { AutoApprove = false, ThrowOnRegister = true };
        var settings = Approving(FederatedTesProfile.ExecutorImage);
        settings.EgressUnreachableGiveUpMinutes = 0;

        await Work(client, Reader(), outputs: outputs, approval: approval, settings: settings, startedAt: Now.AddDays(-1)).Execute();

        var call = Assert.Single(client.Calls);
        Assert.Equal("internal_error", call.Report.ErrorCode);
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

    [Fact]
    public async Task Execute_dataset_bucket_unreachable_soon_after_agent_start_retries_a_long_queued_task()
    {
        // Queued 10 hours ago, for example held while ApprovedImages was missing; the Agent was restarted a minute ago.
        var client = OneMean();
        client.Pending[0].CreationTime = Now.AddMinutes(-600);

        await Work(client, new FailingResolveReader(), startedAt: Now.AddMinutes(-1)).Execute();

        Assert.Empty(client.Reports);
    }

    [Fact]
    public async Task Execute_dataset_bucket_unreachable_gives_up_once_the_agent_has_run_for_the_whole_window()
    {
        var client = OneMean();
        client.Pending[0].CreationTime = Now.AddMinutes(-600);

        await Work(client, new FailingResolveReader(), startedAt: Now.AddMinutes(-60)).Execute();

        Assert.Equal("internal_error", Assert.Single(client.Reports).ErrorCode);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(600)]
    public async Task Execute_dataset_bucket_unreachable_for_too_long_reports_internal_error(int ageMinutes)
    {
        var client = OneMean();
        client.Pending[0].CreationTime = Now.AddMinutes(-ageMinutes);

        await Work(client, new FailingResolveReader()).Execute();

        Assert.Empty(client.ProgressCalls);
        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.SYSTEMERROREnum, report.State);
        Assert.Equal("internal_error", report.ErrorCode);
        Assert.Null(report.RejectedAt);
        Assert.DoesNotContain("store down", report.ErrorMessage);
    }

    [Fact]
    public async Task Execute_dataset_bucket_unreachable_uses_the_configured_give_up_time()
    {
        var client = OneMean();
        client.Pending[0].CreationTime = Now.AddMinutes(-10);

        await Work(client, new FailingResolveReader(), settings: new FederatedTaskSettings { DatasetUnreachableGiveUpMinutes = 5, ApprovedImages = [FederatedTesProfile.ExecutorImage] }).Execute();

        Assert.Equal("internal_error", Assert.Single(client.Reports).ErrorCode);
    }

    [Fact]
    public async Task Execute_dataset_bucket_unreachable_without_creation_time_reports_internal_error()
    {
        var client = OneMean();
        client.Pending[0].CreationTime = null;

        await Work(client, new FailingResolveReader()).Execute();

        Assert.Equal("internal_error", Assert.Single(client.Reports).ErrorCode);
    }

    [Fact]
    public async Task Execute_dataset_read_fails_on_a_young_queued_task_marks_it_running_and_reports_nothing_else()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();

        await Work(client, new FuncReader(() => throw new DatasetStoreException("read failed", new IOException("store down"))), outputs: outputs).Execute();

        // Marked running before the read; the failed read leaves it running for the next poll to resume.
        Assert.Single(client.ProgressCalls);
        Assert.Empty(client.Calls);
        Assert.Empty(outputs.Files);
    }

    [Fact]
    public async Task Execute_dataset_read_fails_on_a_young_running_task_reports_nothing()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;

        await Work(client, new FuncReader(() => throw new DatasetStoreException("read failed", new IOException("store down")))).Execute();

        Assert.Empty(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Execute_dataset_read_fails_for_too_long_reports_internal_error()
    {
        var client = OneMean();
        client.Pending[0].State = TesState.RUNNINGEnum;
        client.Pending[0].CreationTime = Now.AddMinutes(-60);

        await Work(client, new FuncReader(() => throw new DatasetStoreException("read failed", new IOException("store down")))).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.SYSTEMERROREnum, report.State);
        Assert.Equal("internal_error", report.ErrorCode);
        Assert.DoesNotContain("store down", report.ErrorMessage);
    }

    [Fact]
    public async Task Execute_invalid_csv_on_a_young_task_is_still_reported_at_once()
    {
        var client = OneMean();

        await Work(client, new FuncReader(() => DatasetReadResult.Fail("invalid_dataset", "dataset file is not valid CSV"))).Execute();

        Assert.Equal("invalid_dataset", Assert.Single(client.Reports).ErrorCode);
    }

    [Fact]
    public async Task Execute_dataset_read_fault_that_is_not_the_bucket_is_reported_at_once()
    {
        var client = OneMean();

        await Work(client, new FuncReader(() => throw new InvalidOperationException("parser bug"))).Execute();

        // A young task: a bucket failure would be retried, but waiting cannot fix a fault in the TRE.
        Assert.Single(client.ProgressCalls);
        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.SYSTEMERROREnum, report.State);
        Assert.Equal("internal_error", report.ErrorCode);
    }

    /// <summary>Stands in for the task execution; records what it was asked to run.</summary>
    private sealed class FakeExecution(Func<ExecutionResult> run) : IFederatedTaskExecution
    {
        public List<(string? TaskId, FederatedTaskSpec Spec, string FileKey)> Runs { get; } = new();

        public Task<ExecutionResult> RunAsync(TesTask task, FederatedTaskSpec spec, string fileKey, CancellationToken ct)
        {
            Runs.Add((task.Id, spec, fileKey));
            return Task.FromResult(run());
        }
    }

    private static FederatedResultDocument SubstituteDocument() => new()
    {
        TaskId = T1,
        Tre = "tre-a",
        StudyId = "study-1",
        Operation = "mean",
        Dataset = "cohort_2024",
        DatasetSha256 = new string('a', 64),
        CompletedAt = Now,
        Result = new FederatedScalarResult { Value = 42m, RowCount = 7 }
    };

    [Fact]
    public async Task Execute_substituted_execution_result_is_saved_and_released()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var document = SubstituteDocument();
        var execution = new FakeExecution(() => ExecutionResult.Ok(document));

        // The reader only finds the dataset for the input checks; the substitute does not read it.
        await Work(client, new ThrowingReader(), outputs: outputs, execution: execution).Execute();

        var run = Assert.Single(execution.Runs);
        Assert.Equal(T1, run.TaskId);
        Assert.Equal("mean", run.Spec.Operation);
        Assert.Equal("dataset/cohort_2024/data.csv", run.FileKey);
        Assert.Equal(new[] { "progress:running", "report:COMPLETEEnum" }, client.Order);

        var expected = FederatedResultDocumentJson.Serialize(document);
        Assert.Equal(expected, outputs.Files[T1]);
        var call = Assert.Single(client.Calls);
        Assert.Equal(TesState.COMPLETEEnum, call.Report.State);
        Assert.Equal(expected, call.Bytes);
    }

    [Fact]
    public async Task Execute_substituted_execution_error_is_reported_as_executor_error()
    {
        var client = OneMean();
        var outputs = new FakeResultOutputStore();
        var sha = new string('b', 64);
        var execution = new FakeExecution(() => ExecutionResult.Fail("non_numeric_value", "column 'age' contains a non-numeric value", sha));

        await Work(client, new ThrowingReader(), outputs: outputs, execution: execution).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.EXECUTORERROREnum, report.State);
        Assert.Null(report.RejectedAt);
        Assert.Equal("non_numeric_value", report.ErrorCode);
        Assert.Equal("column 'age' contains a non-numeric value", report.ErrorMessage);
        Assert.Equal(sha, report.DatasetSha256);
        Assert.Empty(outputs.Files);
    }

    [Fact]
    public async Task Execute_substituted_execution_does_not_run_when_input_checks_reject_the_task()
    {
        var client = OneMean();
        var execution = new FakeExecution(() => ExecutionResult.Ok(SubstituteDocument()));

        await Work(client, new ThrowingReader(), settings: Approving("other:1"), execution: execution).Execute();

        Assert.Empty(execution.Runs);
        Assert.Equal("image_not_approved", Assert.Single(client.Reports).ErrorCode);
    }

    [Fact]
    public async Task Execute_substituted_execution_store_failure_on_a_young_task_keeps_it_running()
    {
        var client = OneMean();
        var execution = new FakeExecution(() => throw new DatasetStoreException("read failed", new IOException("store down")));

        await Work(client, new ThrowingReader(), execution: execution).Execute();

        Assert.Single(client.ProgressCalls);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Execute_substituted_execution_that_throws_reports_internal_error()
    {
        var client = OneMean();
        var execution = new FakeExecution(() => throw new InvalidOperationException("container crashed"));

        await Work(client, new ThrowingReader(), execution: execution).Execute();

        var report = Assert.Single(client.Reports);
        Assert.Equal(TesState.SYSTEMERROREnum, report.State);
        Assert.Equal("internal_error", report.ErrorCode);
    }

    /// <summary>
    /// Tests that read the log. They swap Serilog's global logger, so they run alone in GlobalLoggerCollection; the rest
    /// of this class runs in parallel as usual.
    /// </summary>
    [Collection(GlobalLoggerCollection.Name)]
    public class LogTests
    {
        private sealed class CollectingSink : Serilog.Core.ILogEventSink
        {
            public List<Serilog.Events.LogEvent> Events { get; } = new();

            public void Emit(Serilog.Events.LogEvent logEvent)
            {
                lock (Events) Events.Add(logEvent);
            }
        }

        /// <summary>Runs the work with a collecting global logger; returns the events whose template contains the text.</summary>
        private static async Task<List<Serilog.Events.LogEvent>> Logged(string text, Func<Task> work)
        {
            var sink = new CollectingSink();
            var previous = Serilog.Log.Logger;
            Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            try
            {
                await work();
            }
            finally
            {
                Serilog.Log.Logger = previous;
            }

            lock (sink.Events) return sink.Events.Where(e => e.MessageTemplate.Text.Contains(text)).ToList();
        }

        private static FakeResultOutputStore SavedResult()
        {
            var outputs = new FakeResultOutputStore();
            outputs.Files[T1] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
            return outputs;
        }

        [Fact]
        public async Task Execute_without_approved_images_logs_once_per_poll()
        {
            var client = new FakeClient();
            client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));
            client.Pending.Add(PendingTask(T2, "cohort_2024", "count", null));
            client.Pending.Add(PendingTask("33333333-3333-3333-3333-333333333333", "cohort_2024", "count", null));

            var events = await Logged("ApprovedImages", () => Work(client, Store(), Approving()).Execute());

            Assert.Equal(Serilog.Events.LogEventLevel.Error, Assert.Single(events).Level);
        }

        [Fact]
        public async Task Execute_without_egress_credentials_logs_once_per_poll()
        {
            var client = new FakeClient();
            client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));
            client.Pending.Add(PendingTask(T2, "cohort_2024", "count", null));

            var events = await Logged("FederatedEgressSettings", () =>
                Work(client, Reader(), approval: new FakeOutputApproval { AutoApprove = false },
                    egress: new FakeEgressClient { IsConfigured = false }).Execute());

            var e = Assert.Single(events);
            Assert.Equal(Serilog.Events.LogEventLevel.Error, e.Level);
            Assert.Equal("2", e.Properties["Count"].ToString());
        }

        [Fact]
        public async Task Execute_saved_result_whose_image_is_no_longer_approved_is_released_with_a_warning()
        {
            var client = new FakeClient();
            client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

            var events = await Logged("no longer approved", () =>
                Work(client, new ThrowingReader(), outputs: SavedResult(), settings: Approving("other:1")).Execute());

            // Releasing is output approval's call, not the input checks'; the site is told the image check was skipped.
            Assert.Equal(TesState.COMPLETEEnum, Assert.Single(client.Reports).State);
            Assert.Equal(Serilog.Events.LogEventLevel.Warning, Assert.Single(events).Level);
        }

        [Fact]
        public async Task Execute_saved_result_whose_image_is_approved_is_released_without_a_warning()
        {
            var client = new FakeClient();
            client.Pending.Add(PendingTask(T1, "cohort_2024", "count", null));

            var events = await Logged("no longer approved", () =>
                Work(client, new ThrowingReader(), outputs: SavedResult()).Execute());

            Assert.Equal(TesState.COMPLETEEnum, Assert.Single(client.Reports).State);
            Assert.Empty(events);
        }

        [Fact]
        public async Task Execute_saved_result_of_a_task_without_an_image_is_released_without_a_warning()
        {
            var client = new FakeClient();
            var task = PendingTask(T1, "cohort_2024", "count", null);
            task.Executors = null;
            client.Pending.Add(task);

            var events = await Logged("no longer approved", () =>
                Work(client, new ThrowingReader(), outputs: SavedResult(), settings: Approving("other:1")).Execute());

            Assert.Equal(TesState.COMPLETEEnum, Assert.Single(client.Reports).State);
            Assert.Empty(events);
        }
    }
}
