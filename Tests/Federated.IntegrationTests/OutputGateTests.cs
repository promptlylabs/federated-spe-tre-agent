using System.Text;
using Xunit;

namespace Federated.IntegrationTests;

/// <summary>The output gate: a hospital reviewer in the Egress app decides before anything leaves.</summary>
[Collection(StackCollection.Name)]
public class OutputGateTests
{
    private readonly StackFixture stack;

    public OutputGateTests(StackFixture stack) => this.stack = stack;

    private async Task<string> WaitingForReviewAsync(string code)
    {
        var queued = await stack.Submission.EnqueueCodeAsync("cohort_2024", code);
        Assert.Equal(200, queued.Status);
        var waiting = await Await.UntilSettledAsync(stack, queued.Id!, Await.PhaseWait);
        Assert.Equal("RUNNING", waiting.State);
        Assert.Equal("waiting_for_output_approval", waiting.Phase);
        return queued.Id!;
    }

    private Task<TaskView> FinalAsync(string id) => Await.UntilAsync(stack, id, t => t.Phase != "waiting_for_output_approval", Await.PhaseWait);

    [SkippableFact]
    public async Task Approved_result_is_released_and_stored_with_the_reviewed_sha256()
    {
        stack.EnsureReady();
        var id = await WaitingForReviewAsync(Code.MeanAge);

        await stack.Egress.DecideAsync(id, approve: true, reason: null);
        var end = await FinalAsync(id);

        Assert.Equal("COMPLETE", end.State);
        Assert.Equal("released", end.Phase);
        var result = await stack.Submission.GetResultAsync(id);
        Assert.NotNull(result);
        Assert.Equal(end.ResultSha256, result.Sha256);
        Assert.True(await stack.Results.HasResultAsync(id));
    }

    [SkippableFact]
    public async Task Reviewer_rejection_is_reported_with_the_reason_and_nothing_is_stored()
    {
        stack.EnsureReady();
        var id = await WaitingForReviewAsync(Code.CountRows);

        await stack.Egress.DecideAsync(id, approve: false, reason: "small cells");
        var end = await FinalAsync(id);

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("rejected_at_output_approval", end.Phase);
        Assert.Equal("rejected_by_reviewer", end.ErrorCode);
        Assert.Equal("small cells", end.ErrorMessage);
        Assert.Null(await stack.Submission.GetResultAsync(id));
        Assert.False(await stack.Results.HasResultAsync(id));
    }

    [SkippableFact]
    public async Task Result_changed_after_registration_is_never_released_even_when_approved()
    {
        stack.EnsureReady();
        var id = await WaitingForReviewAsync(Code.CountRows);

        await stack.Storage.OverwriteResultAsync(id, Encoding.UTF8.GetBytes("{\"tampered\":true}"));
        await stack.Egress.DecideAsync(id, approve: true, reason: null);
        var end = await FinalAsync(id);

        Assert.Equal("SYSTEM_ERROR", end.State);
        Assert.Equal("failed", end.Phase);
        Assert.Equal("result_changed_after_review", end.ErrorCode);
        Assert.Null(await stack.Submission.GetResultAsync(id));
        Assert.False(await stack.Results.HasResultAsync(id));
    }
}
