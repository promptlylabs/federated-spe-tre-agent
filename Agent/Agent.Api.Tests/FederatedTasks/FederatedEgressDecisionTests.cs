using System.Security.Claims;
using Agent.Api.FederatedTasks;
using FiveSafesTes.Core.Models.ViewModels;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedEgressDecisionTests
{
    private const string T1 = "11111111-1111-1111-1111-111111111111";
    private const string Sha = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";
    private const string OtherSha = "ff12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";

    private sealed class FakeApproval : IOutputApproval
    {
        public Dictionary<string, OutputReview> Records { get; } = new();
        public Dictionary<string, ReviewDecision> Decisions { get; } = new();

        public int GetCalls { get; private set; }

        public Task<OutputReview?> GetAsync(string taskId, CancellationToken ct)
        {
            GetCalls++;
            return Task.FromResult(Records.TryGetValue(taskId, out var r) ? r : null);
        }

        public Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<OutputReview> RegisterAsync(EgressRegistration r, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DecisionOutcome> RecordDecisionAsync(string taskId, ReviewDecision d, CancellationToken ct)
        {
            if (!Records.TryGetValue(taskId, out var rec)) return Task.FromResult(DecisionOutcome.NotRegistered);
            if (d.ResultSha256 != rec.ResultSha256) return Task.FromResult(DecisionOutcome.ShaMismatch);
            if (!d.Approved && string.IsNullOrWhiteSpace(d.Reason)) return Task.FromResult(DecisionOutcome.ReasonRequired);
            if (Decisions.TryGetValue(taskId, out var prev))
                return Task.FromResult(prev.Approved == d.Approved ? DecisionOutcome.AlreadyRecorded : DecisionOutcome.ConflictingDecision);
            Decisions[taskId] = d;
            return Task.FromResult(DecisionOutcome.Recorded);
        }
    }

    private static FakeApproval Registered()
    {
        var a = new FakeApproval();
        a.Records[T1] = OutputReview.Pending(Sha, DateTimeOffset.UtcNow);
        return a;
    }

    private static ClaimsPrincipal User(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    private static EgressReview Review(string subId = T1, bool approved = true, string? reason = null, string sha = Sha) => new()
    {
        SubId = subId,
        FileResults = new() { new EgressResult { FileName = "result.json", Approved = approved } },
        Reviewer = "reviewer@hospital.example",
        DecidedAt = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc),
        Reason = reason,
        ResultSha256 = sha
    };

    [Fact]
    public async Task Native_review_is_not_handled()
    {
        var status = await FederatedEgressDecision.TryHandleAsync(Review("42"), User("data-egress-admin"), Registered(), default);
        Assert.Null(status);
    }

    [Fact]
    public async Task Federated_review_from_tre_admin_is_forbidden()
    {
        var approval = Registered();
        var status = await FederatedEgressDecision.TryHandleAsync(Review(), User("dare-tre-admin"), approval, default);
        Assert.Equal(403, status);
        Assert.Empty(approval.Decisions);
    }

    [Fact]
    public async Task Federated_approval_from_egress_admin_is_recorded()
    {
        var approval = Registered();
        var status = await FederatedEgressDecision.TryHandleAsync(Review(), User("data-egress-admin"), approval, default);
        Assert.Equal(200, status);
        Assert.True(approval.Decisions[T1].Approved);
        Assert.Equal(TimeSpan.Zero, approval.Decisions[T1].DecidedAt.Offset);
    }

    [Fact]
    public async Task Federated_rejection_without_reason_is_400()
    {
        var status = await FederatedEgressDecision.TryHandleAsync(Review(approved: false), User("data-egress-admin"), Registered(), default);
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Federated_review_with_other_sha_is_409()
    {
        var status = await FederatedEgressDecision.TryHandleAsync(Review(sha: OtherSha), User("data-egress-admin"), Registered(), default);
        Assert.Equal(409, status);
    }

    [Fact]
    public async Task Tre_admin_cannot_tell_whether_a_task_has_a_review()
    {
        var approval = Registered();
        var unknown = await FederatedEgressDecision.TryHandleAsync(
            Review("22222222-2222-2222-2222-222222222222"), User("dare-tre-admin"), approval, default);
        var known = await FederatedEgressDecision.TryHandleAsync(Review(), User("dare-tre-admin"), approval, default);
        Assert.Equal(403, unknown);
        Assert.Equal(403, known);
        Assert.Equal(0, approval.GetCalls);
    }

    [Fact]
    public async Task Unknown_guid_is_404_not_native()
    {
        var status = await FederatedEgressDecision.TryHandleAsync(
            Review("22222222-2222-2222-2222-222222222222"), User("data-egress-admin"), Registered(), default);
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Repeated_decision_is_200()
    {
        var approval = Registered();
        await FederatedEgressDecision.TryHandleAsync(Review(), User("data-egress-admin"), approval, default);
        var status = await FederatedEgressDecision.TryHandleAsync(Review(), User("data-egress-admin"), approval, default);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Uppercase_guid_is_normalised_before_lookup()
    {
        var status = await FederatedEgressDecision.TryHandleAsync(
            Review(T1.ToUpperInvariant()), User("data-egress-admin"), Registered(), default);
        Assert.Equal(200, status);
    }

    public static TheoryData<string> IncompleteReviews => new() { "no-reviewer", "no-time", "no-sha", "no-files", "two-files" };

    [Theory]
    [MemberData(nameof(IncompleteReviews))]
    public async Task Incomplete_federated_review_is_400_and_not_recorded(string missing)
    {
        var review = Review();
        switch (missing)
        {
            case "no-reviewer": review.Reviewer = " "; break;
            case "no-time": review.DecidedAt = null; break;
            case "no-sha": review.ResultSha256 = ""; break;
            case "no-files": review.FileResults = new(); break;
            case "two-files": review.FileResults.Add(new EgressResult { FileName = "other.json", Approved = true }); break;
        }
        var approval = Registered();

        var status = await FederatedEgressDecision.TryHandleAsync(review, User("data-egress-admin"), approval, default);

        Assert.Equal(400, status);
        Assert.Empty(approval.Decisions);
    }

    [Fact]
    public async Task Local_decision_time_is_converted_to_utc()
    {
        var local = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Local);
        var review = Review();
        review.DecidedAt = local;
        var approval = Registered();

        await FederatedEgressDecision.TryHandleAsync(review, User("data-egress-admin"), approval, default);

        var decidedAt = approval.Decisions[T1].DecidedAt;
        Assert.Equal(TimeSpan.Zero, decidedAt.Offset);
        Assert.Equal(local.ToUniversalTime(), decidedAt.UtcDateTime);
    }

    [Fact]
    public async Task Unspecified_decision_time_is_taken_as_utc()
    {
        var review = Review();
        review.DecidedAt = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Unspecified);
        var approval = Registered();

        await FederatedEgressDecision.TryHandleAsync(review, User("data-egress-admin"), approval, default);

        Assert.Equal(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero), approval.Decisions[T1].DecidedAt);
    }
}
