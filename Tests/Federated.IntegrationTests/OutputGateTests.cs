using System.Text;
using Xunit;

namespace Federated.IntegrationTests;

/// <summary>The output gate: a hospital reviewer in the Egress app decides before anything leaves.</summary>
[Collection(StackCollection.Name)]
public class OutputGateTests
{
    private static readonly TimeSpan Pickup = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Act = TimeSpan.FromSeconds(60);
    private readonly StackFixture stack;

    public OutputGateTests(StackFixture stack) => this.stack = stack;

    private async Task<string> WaitingForReviewAsync(string operation, params string[] args)
    {
        var queued = await stack.Submission.EnqueueAsync("cohort_2024", operation, args);
        Assert.Equal(200, queued.Status);
        var waiting = await Await.UntilAsync(stack, queued.Id!, t => t.State is not ("QUEUED" or "RUNNING"), Pickup);
        Assert.Equal("PAUSED", waiting.State);
        Assert.Equal("waiting_for_output_approval", waiting.Step);
        return queued.Id!;
    }

    private Task<TaskView> FinalAsync(string id) => Await.UntilAsync(stack, id, t => t.State != "PAUSED", Act);

    [SkippableFact]
    public async Task Approved_result_is_released_and_stored_with_the_reviewed_sha256()
    {
        stack.EnsureReady();
        var id = await WaitingForReviewAsync("mean", "--column", "age");

        await stack.Egress.DecideAsync(id, approve: true, reason: null);
        var end = await FinalAsync(id);

        Assert.Equal("COMPLETE", end.State);
        Assert.Equal("released", end.Step);
        var result = await stack.Submission.GetResultAsync(id);
        Assert.NotNull(result);
        Assert.Equal(end.ResultSha256, result.Sha256);
    }

    [SkippableFact]
    public async Task Reviewer_rejection_is_reported_with_the_reason_and_nothing_is_stored()
    {
        stack.EnsureReady();
        var id = await WaitingForReviewAsync("count");

        await stack.Egress.DecideAsync(id, approve: false, reason: "small cells");
        var end = await FinalAsync(id);

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("rejected_at_output_approval", end.Step);
        Assert.Equal("rejected_by_reviewer", end.ErrorCode);
        Assert.Equal("small cells", end.ErrorMessage);
        Assert.Null(await stack.Submission.GetResultAsync(id));
    }

    [SkippableFact]
    public async Task Result_changed_after_registration_is_never_released_even_when_approved()
    {
        stack.EnsureReady();
        var id = await WaitingForReviewAsync("count");

        await stack.Storage.OverwriteResultAsync(id, Encoding.UTF8.GetBytes("{\"tampered\":true}"));
        await stack.Egress.DecideAsync(id, approve: true, reason: null);
        var end = await FinalAsync(id);

        Assert.Equal("SYSTEM_ERROR", end.State);
        Assert.Equal("failed", end.Step);
        Assert.Equal("result_changed_after_review", end.ErrorCode);
        Assert.Null(await stack.Submission.GetResultAsync(id));
    }
}
