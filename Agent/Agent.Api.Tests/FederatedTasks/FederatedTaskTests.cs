using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedTaskTests
{
    private static readonly Guid TaskId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Submitted = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Completed = new(2026, 10, 26, 9, 5, 0, DateTimeKind.Utc);

    private static FederatedTask Make(TesState state)
    {
        var tes = new TesTask
        {
            Executors = [new TesExecutor { Image = "tre", Command = ["mean", "age"] }]
        };
        return In(new FederatedTask
        {
            TaskId = TaskId,
            StudyId = "study",
            Operation = "mean",
            Dataset = "cohort",
            TesTaskJson = JsonConvert.SerializeObject(tes),
            SubmittedAt = Submitted
        }, state);
    }

    private static FederatedTask In(FederatedTask task, TesState state)
    {
        task.State = state;
        task.Step = state switch
        {
            TesState.COMPLETEEnum => FederatedTaskStep.Released,
            TesState.EXECUTORERROREnum or TesState.SYSTEMERROREnum => FederatedTaskStep.Failed,
            _ => FederatedTaskStep.Queued
        };
        return task;
    }

    [Fact]
    public void ToTesTask_Queued_HasOnlyStepMetadata()
    {
        var task = Make(TesState.QUEUEDEnum).ToTesTask();

        Assert.Equal(TaskId.ToString(), task.Id);
        Assert.Equal(TesState.QUEUEDEnum, task.State);
        var log = Assert.Single(task.Logs!);
        Assert.Equal(["step"], log.Metadata!.Keys.ToArray());
        Assert.Equal("queued", log.Metadata["step"]);
        Assert.Null(log.EndTime);
        Assert.Equal(new DateTimeOffset(Submitted, TimeSpan.Zero), task.CreationTime);
        Assert.Equal(["mean", "age"], task.Executors![0].Command);
    }

    [Fact]
    public void ToTesTask_Complete_HasResultLocationMetadata()
    {
        var entity = Make(TesState.COMPLETEEnum);
        entity.CompletedAt = Completed;
        entity.ResultBucket = "federated-results";
        entity.ResultKey = "1/key.json";
        entity.ResultSha256 = new string('a', 64);

        var log = Assert.Single(entity.ToTesTask().Logs!);

        Assert.Equal("federated-results", log.Metadata!["result_bucket"]);
        Assert.Equal("1/key.json", log.Metadata["result_key"]);
        Assert.Equal(new string('a', 64), log.Metadata["result_sha256"]);
        Assert.False(log.Metadata.ContainsKey("value"));
        Assert.Equal("released", log.Metadata["step"]);
    }

    [Fact]
    public void ToTesTask_ExecutorError_HasErrorMetadata()
    {
        var entity = Make(TesState.EXECUTORERROREnum);
        entity.CompletedAt = Completed;
        entity.ErrorCode = "dataset_not_found";
        entity.ResultErrorMessage = "no such dataset";

        var log = Assert.Single(entity.ToTesTask().Logs!);

        Assert.Equal("dataset_not_found", log.Metadata!["error_code"]);
        Assert.Equal("no such dataset", log.Metadata["error_message"]);
        Assert.False(log.Metadata.ContainsKey("value"));
    }

    [Theory]
    [InlineData(TesState.COMPLETEEnum)]
    [InlineData(TesState.EXECUTORERROREnum)]
    public void ToTesTask_WithDatasetSha256_EmitsIt(TesState state)
    {
        var entity = Make(state);
        entity.DatasetSha256 = new string('a', 64);

        var log = Assert.Single(entity.ToTesTask().Logs!);

        Assert.Equal(new string('a', 64), log.Metadata!["dataset_sha256"]);
    }

    [Fact]
    public void ToTesTask_WithoutDatasetSha256_OmitsIt()
    {
        var log = Assert.Single(Make(TesState.COMPLETEEnum).ToTesTask().Logs!);

        Assert.False(log.Metadata!.ContainsKey("dataset_sha256"));
    }
}
