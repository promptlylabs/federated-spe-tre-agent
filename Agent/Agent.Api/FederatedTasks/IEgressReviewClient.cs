namespace Agent.Api.FederatedTasks
{
    /// <summary>What the hospital reviewer sees in the Egress app for one federated task result.</summary>
    public sealed record EgressRegistration(
        string TaskId,
        string Name,
        string StudyId,
        string Code,
        string ResultContent,
        string ResultSha256);

    /// <summary>Registers a federated result with the Data Egress app for review.</summary>
    public interface IEgressReviewClient
    {
        bool IsConfigured { get; }

        /// <summary>Succeeds also when the result is already registered. Throws on any other failure.</summary>
        Task RegisterAsync(EgressRegistration r, CancellationToken ct);
    }
}
