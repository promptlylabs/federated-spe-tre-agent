using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedTaskExecutionRouterTests
{
    private const string T1 = "6f1c2b9a-4d3e-4f5a-9b8c-7d6e5f4a3b2c";
    private const string CsvKey = "dataset/cohort_2024/cohort.csv";

    private readonly FakeCodeRunner _runner = new();
    private readonly FakeDatasetStore _store = new FakeDatasetStore()
        .Add(CsvKey, "age\n41\n52\n")
        .Add("dataset/cohort_2024/schema.json", "{}");

    private FederatedTaskExecutionRouter Router()
    {
        var settings = new FederatedTaskSettings { MaxConcurrentRuns = 1 };
        var operations = new OperationExecution(new DatasetReader(_store, settings), TimeProvider.System);
        var code = new CodeExecution(_runner, _store, new NoOutputs(), settings, TimeProvider.System);
        return new FederatedTaskExecutionRouter(operations, code);
    }

    private static readonly FederatedTaskSpec CountSpec = new("count", "cohort_2024", null, [], "study-1", "tre-a");
    private static readonly FederatedTaskSpec CodeSpec = new("run-analysis", "cohort_2024", null, [], "study-1", "tre-a", new string('a', 64));

    private static TesTask AnyTask() => new()
    {
        Id = T1,
        Executors = [new TesExecutor { Image = "python-runner:1" }],
        Inputs = [new TesInput { Path = "/code/analysis.py", Type = TesFileType.FILEEnum, Content = "x = 1\n" }]
    };

    [Fact]
    public async Task Routes_count_to_operations_and_code_to_the_runner()
    {
        var router = Router();

        var count = await router.RunAsync(AnyTask(), CountSpec, CsvKey, CancellationToken.None);
        Assert.True(count.IsSuccess);
        Assert.Equal(2, count.Document!.Result.RowCount);
        Assert.Empty(_runner.Started);

        Assert.True(await router.CanStartAsync(CountSpec, CancellationToken.None));
        Assert.True(await router.CanStartAsync(CodeSpec, CancellationToken.None));

        var code = await router.RunAsync(AnyTask(), CodeSpec, CsvKey, CancellationToken.None);
        Assert.True(code.IsInProgress);
        Assert.Single(_runner.Started);

        // The run limit applies to code tasks only.
        Assert.True(await router.CanStartAsync(CountSpec, CancellationToken.None));
        Assert.False(await router.CanStartAsync(CodeSpec, CancellationToken.None));
    }

    [Fact]
    public async Task Run_members_go_to_the_code_runner()
    {
        var router = Router();
        Assert.False(await router.HasRunAsync(T1, CancellationToken.None));

        await router.RunAsync(AnyTask(), CodeSpec, CsvKey, CancellationToken.None);
        Assert.True(await router.HasRunAsync(T1, CancellationToken.None));

        await router.RemoveRunAsync(T1, CancellationToken.None);
        Assert.Equal([T1], _runner.Removed);

        _runner.States[T1] = new CodeRunState(CodeRunStatus.Running, DateTimeOffset.UtcNow, null, null, false, null);
        _runner.CreatedAt[T1] = DateTimeOffset.UtcNow.AddDays(-1);
        await router.SweepAsync([], CancellationToken.None);
        Assert.Equal([T1, T1], _runner.Removed);
    }

    private sealed class NoOutputs : IResultOutputStore
    {
        public Task<byte[]?> TryGetAsync(string taskId, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Task SaveAsync(string taskId, byte[] content, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task MarkReleasedAsync(string taskId, string resultKey, string resultSha256, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
    }
}
