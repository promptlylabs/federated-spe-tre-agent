namespace Agent.Api.FederatedTasks
{
    /// <summary>What the hospital reviewer sees in the Egress app for one federated task result.</summary>
    public sealed record EgressRegistration(
        string TaskId,
        string Name,
        string StudyId,
        string Code,
        string ResultContent,
        string ResultSha256,
        string OutputBucket);

    /// <summary>Registers a federated result with the Data Egress app for review.</summary>
    public interface IEgressReviewClient
    {
        bool IsConfigured { get; }

        /// <summary>Succeeds also when the result is already registered. Throws on any other failure.</summary>
        Task RegisterAsync(EgressRegistration r, CancellationToken ct);

        /// <summary>
        /// Closes the task's undecided request in the Egress app with a reason, so reviewers can no longer decide it.
        /// Succeeds also when there is no such request. Throws on any other failure.
        /// </summary>
        Task WithdrawAsync(string taskId, string reason, CancellationToken ct);
    }
}
