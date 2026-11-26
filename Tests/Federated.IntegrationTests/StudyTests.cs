using Xunit;

namespace Federated.IntegrationTests;

/// <summary>
/// A study version sent to my-tre and other-tre. other-tre has no Agent on this stack, so its part stays queued: the
/// version stays open until it is cancelled.
/// </summary>
[Collection(StackCollection.Name)]
public class StudyTests
{
    private const string BothHospitals = $"{StackSettings.Tre}|{StackSettings.OtherTre}";
    private readonly StackFixture stack;

    public StudyTests(StackFixture stack) => this.stack = stack;

    private Task<EnqueueOutcome> EnqueueAsync(string studyId) =>
        stack.Submission.EnqueueCodeAsync("cohort_2024", Code.CountRows, sites: BothHospitals, studyId: studyId);

    /// <summary>Enqueues version 1 for both hospitals and waits until my-tre's part waits for review.</summary>
    private async Task<EnqueueOutcome> WaitingAtMyTreAsync(string studyId)
    {
        var v1 = await EnqueueAsync(studyId);
        Assert.Equal(200, v1.Status);
        var waiting = await Await.UntilSettledAsync(stack, v1.Id!, Await.PhaseWait);
        Assert.Equal("waiting_for_output_approval", waiting.Phase);
        return v1;
    }

    [SkippableFact]
    public async Task Version_reaches_each_hospital_and_shows_their_phases()
    {
        stack.EnsureReady();
        var studyId = $"study-{Guid.NewGuid():N}";
        var v1 = await WaitingAtMyTreAsync(studyId);

        await stack.Egress.DecideAsync(v1.Id!, approve: true, reason: null);
        var released = await Await.UntilAsync(stack, v1.Id!, t => t.Phase != "waiting_for_output_approval", Await.PhaseWait);
        var study = await stack.Submission.GetStudyVersionAsync(v1.VersionId!);
        var again = await EnqueueAsync(studyId);

        Assert.Equal("released", released.Phase);
        Assert.Equal("open", study.State);
        Assert.Equal("released", study.PhaseOf(StackSettings.Tre));
        Assert.Equal("queued", study.PhaseOf(StackSettings.OtherTre));
        Assert.Equal((409, "study_version_open", v1.VersionId), (again.Status, again.ErrorCode, again.VersionId));

        var cancel = await stack.Submission.CancelAsync(v1.VersionId!);
        var ended = await stack.Submission.GetStudyVersionAsync(v1.VersionId!);
        var v2 = await EnqueueAsync(studyId);

        Assert.Equal((200, "ended"), cancel);
        Assert.Equal("canceled", ended.PhaseOf(StackSettings.OtherTre));
        Assert.Equal("released", ended.PhaseOf(StackSettings.Tre));
        Assert.Equal(200, v2.Status);
        Assert.Equal(2, (await stack.Submission.GetStudyVersionAsync(v2.VersionId!)).Version);
        await stack.Submission.CancelAsync(v2.VersionId!);
    }

    [SkippableFact]
    public async Task Cancel_while_waiting_lets_a_new_version_start_at_once()
    {
        stack.EnsureReady();
        var studyId = $"study-{Guid.NewGuid():N}";
        var v1 = await WaitingAtMyTreAsync(studyId);

        var cancel = await stack.Submission.CancelAsync(v1.VersionId!);
        var v2 = await EnqueueAsync(studyId);
        await stack.Submission.CancelAsync(v2.VersionId!);

        Assert.Equal((200, "canceling"), cancel);
        Assert.Equal(200, v2.Status);

        await Await.UntilAsync(stack, v1.Id!, t => t.Phase != "canceling", Await.PhaseWait);
        var ended = await stack.Submission.GetStudyVersionAsync(v1.VersionId!);

        Assert.Equal("ended", ended.State);
        Assert.Equal("canceled", ended.PhaseOf(StackSettings.Tre));
        Assert.Equal("canceled", ended.PhaseOf(StackSettings.OtherTre));
    }
}
