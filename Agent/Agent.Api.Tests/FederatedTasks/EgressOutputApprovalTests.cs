using Agent.Api.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class EgressOutputApprovalTests
{
    private const string TaskId = "11111111-1111-1111-1111-111111111111";
    private const string Sha = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";
    private const string OtherSha = "ff12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DecidedAt = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : IOutputReviewStore
    {
        public readonly Dictionary<string, OutputReview> Records = new();
        public readonly HashSet<string> Markers = new();
        public int Saves;

        public Task<OutputReview?> GetAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(Records.TryGetValue(taskId, out var r) ? r : null);

        public Task SaveAsync(string taskId, OutputReview review, CancellationToken ct)
        {
            Saves++;
            Records[taskId] = review;
            return Task.CompletedTask;
        }

        public Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(Markers.Contains(taskId));

        public Task MarkRegisteredAsync(string taskId, CancellationToken ct)
        {
            Markers.Add(taskId);
            return Task.CompletedTask;
        }

        public readonly HashSet<string> Withdrawn = new();

        public Task<bool> IsWithdrawnAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(Withdrawn.Contains(taskId));

        public Task MarkWithdrawnAsync(string taskId, CancellationToken ct)
        {
            Withdrawn.Add(taskId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEgress : IEgressReviewClient
    {
        public bool IsConfigured => true;
        public int Calls;
        public Func<EgressRegistration, Task>? OnRegister;

        public async Task RegisterAsync(EgressRegistration r, CancellationToken ct)
        {
            Calls++;
            if (OnRegister != null)
                await OnRegister(r);
        }

        public List<(string TaskId, string Reason)> Withdrawals = new();
        public bool ThrowOnWithdraw;

        public Task WithdrawAsync(string taskId, string reason, CancellationToken ct)
        {
            if (ThrowOnWithdraw) throw new HttpRequestException("egress down");
            Withdrawals.Add((taskId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static EgressRegistration Registration(string sha = Sha) =>
        new(TaskId, "Count by sex", "study-1", "print(1)", "{\"n\":1}", sha, "federated-outputs");

    private static ReviewDecision Approve(string? reason = null, string sha = Sha) =>
        new(true, "reviewer@hospital.example", DecidedAt, reason, sha);

    private static ReviewDecision Reject(string? reason, string sha = Sha) =>
        new(false, "reviewer@hospital.example", DecidedAt, reason, sha);

    private static (EgressOutputApproval approval, FakeStore store, FakeEgress egress) Make()
    {
        var store = new FakeStore();
        var egress = new FakeEgress();
        return (new EgressOutputApproval(store, egress, new FixedClock()), store, egress);
    }

    private static (EgressOutputApproval approval, FakeStore store) Registered()
    {
        var (approval, store, _) = Make();
        store.Records[TaskId] = OutputReview.Pending(Sha, Now);
        store.Markers.Add(TaskId);
        return (approval, store);
    }

    [Fact]
    public async Task Register_writes_the_pending_record_before_calling_egress()
    {
        var (approval, store, egress) = Make();
        OutputReview? seen = null;
        egress.OnRegister = _ =>
        {
            seen = store.Records.GetValueOrDefault(TaskId);
            return Task.CompletedTask;
        };

        await approval.RegisterAsync(Registration(), CancellationToken.None);

        Assert.Equal(OutputReview.Pending(Sha, Now), seen);
    }

    [Fact]
    public async Task Register_writes_the_marker_after_egress_accepts_and_leaves_the_record_pending()
    {
        var (approval, store, egress) = Make();
        var markedBeforeEgress = true;
        egress.OnRegister = _ =>
        {
            markedBeforeEgress = store.Markers.Contains(TaskId);
            return Task.CompletedTask;
        };

        var returned = await approval.RegisterAsync(Registration(), CancellationToken.None);

        Assert.False(markedBeforeEgress);
        Assert.Contains(TaskId, store.Markers);
        Assert.Equal(OutputReview.Pending(Sha, Now), store.Records[TaskId]);
        Assert.Equal(store.Records[TaskId], returned);
        Assert.Equal(1, store.Saves);
        Assert.Equal(1, egress.Calls);
        Assert.True(await approval.IsRegisteredAsync(TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task Register_failure_leaves_a_pending_record_and_no_marker()
    {
        var (approval, store, egress) = Make();
        egress.OnRegister = _ => throw new HttpRequestException("egress down");

        await Assert.ThrowsAsync<HttpRequestException>(() => approval.RegisterAsync(Registration(), CancellationToken.None));

        Assert.True(store.Records[TaskId].IsPending);
        Assert.Empty(store.Markers);
        Assert.False(await approval.IsRegisteredAsync(TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task Register_again_after_a_failure_keeps_the_record_and_writes_the_marker()
    {
        var (approval, store, egress) = Make();
        store.Records[TaskId] = OutputReview.Pending(Sha, Now.AddMinutes(-5));

        var returned = await approval.RegisterAsync(Registration(), CancellationToken.None);

        Assert.Equal(1, egress.Calls);
        Assert.Equal(0, store.Saves);
        Assert.Equal(OutputReview.Pending(Sha, Now.AddMinutes(-5)), returned);
        Assert.Contains(TaskId, store.Markers);
    }

    [Fact]
    public async Task Register_does_not_call_egress_for_a_decided_review()
    {
        var (approval, store, egress) = Make();
        var decided = new OutputReview(Sha, Now, DisclosureCheckStatus.Completed, "reviewer", DecidedAt);
        store.Records[TaskId] = decided;

        var returned = await approval.RegisterAsync(Registration(), CancellationToken.None);

        Assert.Equal(0, egress.Calls);
        Assert.Equal(0, store.Saves);
        Assert.Equal(decided, returned);
    }

    [Fact]
    public async Task Decision_recorded_while_egress_registers_is_not_overwritten_by_the_registration()
    {
        var (approval, store, egress) = Make();
        DecisionOutcome? outcome = null;
        var savesAfterDecision = -1;
        egress.OnRegister = async _ =>
        {
            outcome = await approval.RecordDecisionAsync(TaskId, Approve(), CancellationToken.None);
            savesAfterDecision = store.Saves;
        };

        var returned = await approval.RegisterAsync(Registration(), CancellationToken.None);

        Assert.Equal(DecisionOutcome.Recorded, outcome);
        Assert.Equal(savesAfterDecision, store.Saves);
        var stored = store.Records[TaskId];
        Assert.True(stored.IsApproved);
        Assert.Equal("reviewer@hospital.example", stored.Agent);
        Assert.Contains(TaskId, store.Markers);
        Assert.Equal(stored, returned);
    }

    [Fact]
    public async Task Decision_does_not_write_the_marker()
    {
        var (approval, store, _) = Make();
        store.Records[TaskId] = OutputReview.Pending(Sha, Now);

        await approval.RecordDecisionAsync(TaskId, Approve(), CancellationToken.None);

        Assert.True(store.Records[TaskId].IsApproved);
        Assert.Empty(store.Markers);
    }

    [Fact]
    public async Task Decision_for_unknown_task_is_not_registered()
    {
        var (approval, store, _) = Make();

        var outcome = await approval.RecordDecisionAsync(TaskId, Approve(), CancellationToken.None);

        Assert.Equal(DecisionOutcome.NotRegistered, outcome);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task Decision_with_other_sha_is_refused()
    {
        var (approval, store) = Registered();
        var before = store.Records[TaskId];

        var outcome = await approval.RecordDecisionAsync(TaskId, Approve(sha: OtherSha), CancellationToken.None);

        Assert.Equal(DecisionOutcome.ShaMismatch, outcome);
        Assert.Equal(before, store.Records[TaskId]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Rejection_without_reason_is_refused(string? reason)
    {
        var (approval, store) = Registered();
        var before = store.Records[TaskId];

        var outcome = await approval.RecordDecisionAsync(TaskId, Reject(reason), CancellationToken.None);

        Assert.Equal(DecisionOutcome.ReasonRequired, outcome);
        Assert.Equal(before, store.Records[TaskId]);
    }

    [Fact]
    public async Task Approval_is_recorded_as_completed()
    {
        var (approval, store) = Registered();

        var outcome = await approval.RecordDecisionAsync(TaskId, Approve(), CancellationToken.None);

        Assert.Equal(DecisionOutcome.Recorded, outcome);
        var stored = store.Records[TaskId];
        Assert.True(stored.IsApproved);
        Assert.Equal("reviewer@hospital.example", stored.Agent);
        Assert.Equal(DecidedAt, stored.EndTime);
        Assert.Null(stored.Description);
        Assert.Equal(Now, stored.StartTime);
    }

    [Fact]
    public async Task Approval_keeps_its_optional_comment()
    {
        var (approval, store) = Registered();

        await approval.RecordDecisionAsync(TaskId, Approve("Checked counts"), CancellationToken.None);

        Assert.Equal("Checked counts", store.Records[TaskId].Description);
    }

    [Fact]
    public async Task Rejection_is_recorded_as_failed_with_its_reason()
    {
        var (approval, store) = Registered();

        var outcome = await approval.RecordDecisionAsync(TaskId, Reject("Small cell counts"), CancellationToken.None);

        Assert.Equal(DecisionOutcome.Recorded, outcome);
        var stored = store.Records[TaskId];
        Assert.True(stored.IsRejected);
        Assert.Equal("Small cell counts", stored.Description);
        Assert.Equal("reviewer@hospital.example", stored.Agent);
        Assert.Equal(DecidedAt, stored.EndTime);
    }

    [Fact]
    public async Task Same_decision_twice_is_already_recorded()
    {
        var (approval, store) = Registered();
        await approval.RecordDecisionAsync(TaskId, Reject("Small cell counts"), CancellationToken.None);
        var first = store.Records[TaskId];
        var saves = store.Saves;

        var outcome = await approval.RecordDecisionAsync(TaskId, Reject("Small cell counts"), CancellationToken.None);

        Assert.Equal(DecisionOutcome.AlreadyRecorded, outcome);
        Assert.Equal(saves, store.Saves);
        Assert.Equal(first, store.Records[TaskId]);
    }

    [Fact]
    public async Task Different_second_decision_is_refused()
    {
        var (approval, store) = Registered();
        await approval.RecordDecisionAsync(TaskId, Approve(), CancellationToken.None);
        var first = store.Records[TaskId];

        var outcome = await approval.RecordDecisionAsync(TaskId, Reject("Changed my mind"), CancellationToken.None);

        Assert.Equal(DecisionOutcome.ConflictingDecision, outcome);
        Assert.Equal(first, store.Records[TaskId]);
    }

    [Fact]
    public async Task Get_returns_the_stored_record()
    {
        var (approval, store) = Registered();

        Assert.Equal(store.Records[TaskId], await approval.GetAsync(TaskId, CancellationToken.None));
        Assert.Null(await approval.GetAsync("other", CancellationToken.None));
    }

    [Fact]
    public async Task Withdraw_marks_the_task_and_closes_the_egress_request()
    {
        var (approval, store, egress) = Make();
        store.Records[TaskId] = OutputReview.Pending(Sha, Now);

        await approval.WithdrawAsync(TaskId, "changed", default);

        Assert.Contains(TaskId, store.Withdrawn);
        Assert.Equal((TaskId, "changed"), Assert.Single(egress.Withdrawals));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Withdraw_still_marks_the_task_when_the_egress_app_cannot_be_reached()
    {
        var (approval, store, egress) = Make();
        egress.ThrowOnWithdraw = true;

        await approval.WithdrawAsync(TaskId, "changed", default);

        Assert.Contains(TaskId, store.Withdrawn);
    }

    [Fact]
    public async Task Decision_on_a_withdrawn_task_is_refused_and_not_saved()
    {
        var (approval, store) = Registered();
        store.Withdrawn.Add(TaskId);

        var outcome = await approval.RecordDecisionAsync(TaskId, Approve(), default);

        Assert.Equal(DecisionOutcome.Withdrawn, outcome);
        Assert.True(store.Records[TaskId].IsPending);
        Assert.Equal(0, store.Saves);
    }
}
