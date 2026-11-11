namespace Agent.Api.FederatedTasks
{
    public enum DecisionOutcome
    {
        Recorded,
        AlreadyRecorded,
        NotRegistered,
        ShaMismatch,
        ConflictingDecision,
        ReasonRequired
    }

    /// <summary>A reviewer's decision on one result. A rejection must have a reason; for an approval it is an optional comment.</summary>
    public sealed record ReviewDecision(
        bool Approved,
        string Reviewer,
        DateTimeOffset DecidedAt,
        string? Reason,
        string ResultSha256);

    /// <summary>Holds each federated result until a hospital reviewer approves or rejects it.</summary>
    public interface IOutputApproval
    {
        Task<OutputReview?> GetAsync(string taskId, CancellationToken ct);
        Task<OutputReview> RegisterAsync(EgressRegistration r, CancellationToken ct);
        Task<DecisionOutcome> RecordDecisionAsync(string taskId, ReviewDecision d, CancellationToken ct);
    }
}
