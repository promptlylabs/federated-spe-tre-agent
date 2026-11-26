namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// A validated federated task over one dataset: a code task that runs the researcher's file with run-analysis
    /// (<see cref="Operation"/> "run-analysis"). <see cref="CodeSha256"/> is the hash of that file.
    /// </summary>
    public sealed record FederatedTaskSpec(
        string Operation,
        string Dataset,
        string StudyId,
        string TreName,
        string CodeSha256);

    /// <summary>
    /// Outcome of parsing a TES task against the federated profile: either a spec or an error code and message.
    /// </summary>
    public sealed record FederatedTaskParseResult(FederatedTaskSpec? Spec, string? ErrorCode, string? ErrorMessage)
    {
        public bool IsSuccess => Spec != null;
    }
}
