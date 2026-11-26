using Xunit;

namespace Federated.IntegrationTests;

/// <summary>
/// A study version sent to my-tre and other-tre. other-tre has no Agent on this stack, so its part stays queued: the
/// version stays open while my-tre can still release, until it is cancelled or its deadline (30 minutes here) passes. When
/// my-tre is rejected the 2-hospital minimum can't be met, and the sweep ends the version incomplete.
/// </summary>
[Collection(StackCollection.Name)]
public class StudyTests
{
    private const string BothHospitals = StackSettings.BothHospitals;
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

    [SkippableFact]
    public async Task One_hospital_is_refused_at_intake()
    {
        stack.EnsureReady();

        var outcome = await stack.Submission.EnqueueCodeAsync("cohort_2024", Code.CountRows, sites: StackSettings.Tre);

        Assert.Equal((400, "invalid_task"), (outcome.Status, outcome.ErrorCode));
    }

    /// <summary>Demo refusal R2: the hospital's reviewer rejects, so only other-tre could still release; that is not 2.</summary>
    [SkippableFact]
    public async Task Rejected_review_ends_the_study_incomplete()
    {
        stack.EnsureReady();
        var studyId = $"study-{Guid.NewGuid():N}";
        var v1 = await WaitingAtMyTreAsync(studyId);

        await stack.Egress.DecideAsync(v1.Id!, approve: false, reason: "small cells");
        var ended = await Await.UntilStudyAsync(stack, v1.VersionId!, s => s.Outcome != null, TimeSpan.FromMinutes(2));
        var v2 = await EnqueueAsync(studyId);
        await stack.Submission.CancelAsync(v2.VersionId!);

        Assert.Equal(("incomplete", "minimum_unreachable", "ended"), (ended.Outcome, ended.OutcomeReason, ended.State));
        Assert.Contains("at least 2", ended.OutcomeMessage);
        Assert.Equal("rejected_at_output_approval", ended.PhaseOf(StackSettings.Tre));
        Assert.Equal("canceled", ended.PhaseOf(StackSettings.OtherTre));
        Assert.Equal(200, v2.Status);

        // 3.4: the audit trail says why there is no pooled result, and GetStudyResult has nothing to give.
        var audit = await stack.Submission.GetStudyAuditAsync(v1.VersionId!);
        Assert.Equal(("incomplete", "minimum_unreachable"), (audit["outcome"]!.GetValue<string>(), audit["outcome_reason"]!.GetValue<string>()));
        Assert.Null(audit["pooled_result_sha256"]);
        Assert.Equal(404, (await stack.Submission.GetStudyResultAsync(v1.VersionId!)).Status);
    }
}
