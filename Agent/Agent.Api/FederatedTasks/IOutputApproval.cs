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

        /// <summary>
        /// True when the Egress app has accepted the registration. Only meaningful for a pending review: a decided
        /// review came from the Egress app, so it is registered whatever this returns.
        /// </summary>
        Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct);

        Task<OutputReview> RegisterAsync(EgressRegistration r, CancellationToken ct);
        Task<DecisionOutcome> RecordDecisionAsync(string taskId, ReviewDecision d, CancellationToken ct);
    }
}
