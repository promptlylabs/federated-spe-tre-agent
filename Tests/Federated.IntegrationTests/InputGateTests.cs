using Xunit;

namespace Federated.IntegrationTests;

/// <summary>Tasks the input gates stop: the hospital's input checks, or Submission's task profile.</summary>
[Collection(StackCollection.Name)]
public class InputGateTests
{
    private readonly StackFixture stack;

    public InputGateTests(StackFixture stack) => this.stack = stack;

    [SkippableFact]
    public async Task Hospital_input_checks_stop_a_task_for_a_dataset_the_hospital_does_not_hold()
    {
        Skip.IfNot(stack.Available, stack.UnavailableReason);
        var queued = await stack.Submission.EnqueueAsync($"missing_{Guid.NewGuid():N}", "count");
        Assert.Equal(200, queued.Status);

        var end = await Await.UntilAsync(stack, queued.Id!, t => t.State != "QUEUED" && t.State != "RUNNING", TimeSpan.FromSeconds(120));

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("rejected_by_input_checks", end.Step);
        Assert.Equal("dataset_not_found", end.ErrorCode);
        Assert.Null(await stack.Submission.GetResultAsync(queued.Id!));
    }
}
