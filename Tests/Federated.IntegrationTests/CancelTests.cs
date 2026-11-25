using Xunit;

namespace Federated.IntegrationTests;

/// <summary>The orchestrator cancels a study version: my-tre closes the review, removes the result and confirms; other-tre's queued part ends at once.</summary>
[Collection(StackCollection.Name)]
public class CancelTests
{
    private readonly StackFixture stack;

    public CancelTests(StackFixture stack) => this.stack = stack;

    private async Task<EnqueueOutcome> WaitingForReviewAsync()
    {
        var queued = await stack.Submission.EnqueueCodeAsync("cohort_2024", Code.CountRows);
        Assert.Equal(200, queued.Status);
        var waiting = await Await.UntilSettledAsync(stack, queued.Id!, Await.PhaseWait);
        Assert.Equal("waiting_for_output_approval", waiting.Phase);
        return queued;
    }

    private Task<TaskView> CanceledAsync(string id) => Await.UntilAsync(stack, id, t => t.Phase != "canceling", Await.PhaseWait);

    private async Task AssertNothingLeftAsync(string id)
    {
        Assert.False(await stack.Egress.HasOpenRequestAsync(id));
        Assert.False(await stack.Storage.HasOutputAsync(id));
        Assert.Null(await stack.Submission.GetResultAsync(id));
        Assert.False(await stack.Results.HasResultAsync(id));
    }

    [SkippableFact]
    public async Task Cancel_while_waiting_for_review_closes_the_review_and_removes_the_result()
    {
        stack.EnsureReady();
        var queued = await WaitingForReviewAsync();
        var id = queued.Id!;
        Assert.True(await stack.Storage.HasOutputAsync(id));

        var cancel = await stack.Submission.CancelAsync(queued.VersionId!);
        var end = await CanceledAsync(id);

        Assert.Equal((200, "canceling"), cancel);
        Assert.Equal("CANCELED", end.State);
        Assert.Equal("canceled", end.Phase);
        await AssertNothingLeftAsync(id);
    }

    [SkippableFact]
    public async Task Cancel_while_running_ends_canceled()
    {
        // Stopping and removing the run container is covered by the Agent's DoFederatedTaskWork and DockerCodeRunner tests.
        stack.EnsureReady();
        var queued = await stack.Submission.EnqueueCodeAsync("cohort_2024", Code.Sleep);
        Assert.Equal(200, queued.Status);
        var running = await Await.UntilAsync(stack, queued.Id!, t => t.Phase != "queued", Await.PhaseWait);
        Assert.Equal("running", running.Phase);

        var cancel = await stack.Submission.CancelAsync(queued.VersionId!);
        var end = await CanceledAsync(queued.Id!);

        Assert.Equal((200, "canceling"), cancel);
        Assert.Equal("CANCELED", end.State);
        Assert.Equal("canceled", end.Phase);
        await AssertNothingLeftAsync(queued.Id!);
    }

    [SkippableFact]
    public async Task Approval_at_the_same_time_as_a_cancel_never_releases_the_result()
    {
        stack.EnsureReady();
        var queued = await WaitingForReviewAsync();
        var id = queued.Id!;

        await stack.Submission.CancelAsync(queued.VersionId!);
        try
        {
            await stack.Egress.DecideAsync(id, approve: true, reason: null);
        }
        catch (InvalidOperationException)
        {
            // The hospital already withdrew the review, so the decision is refused: also a valid order of events.
        }
        var end = await CanceledAsync(id);

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("canceled", end.Phase);
        await AssertNothingLeftAsync(id);
    }

    [SkippableFact]
    public async Task Cancel_of_a_queued_task_ends_it_before_any_hospital_picks_it_up()
    {
        stack.EnsureReady();
        var queued = await stack.Submission.EnqueueCodeAsync("cohort_2024", Code.CountRows);
        Assert.Equal(200, queued.Status);

        var cancel = await stack.Submission.CancelAsync(queued.VersionId!);
        await stack.Agent.TriggerAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));
        var end = await stack.Submission.GetTaskAsync(queued.Id!);

        Assert.Equal((200, "ended"), cancel);
        Assert.Equal("CANCELED", end.State);
        Assert.Equal("canceled", end.Phase);
        Assert.False(await stack.Storage.HasOutputAsync(queued.Id!));
    }
}
