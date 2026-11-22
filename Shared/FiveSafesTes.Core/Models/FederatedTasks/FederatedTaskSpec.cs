namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// A validated federated task over one dataset: either one allow-listed operation (count, mean), or a code task
    /// that runs the researcher's file with run-analysis (Operation "run-analysis", CodeSha256 set).
    /// </summary>
    public sealed record FederatedTaskSpec(
        string Operation,
        string Dataset,
        string? Column,
        IReadOnlyList<TaskFilter> Filters,
        string StudyId,
        string TreName,
        string? CodeSha256 = null)
    {
        public bool IsCodeTask => CodeSha256 != null;
    }

    /// <summary>
    /// Outcome of parsing a TES task against the federated profile: either a spec or an error code and message.
    /// </summary>
    public sealed record FederatedTaskParseResult(FederatedTaskSpec? Spec, string? ErrorCode, string? ErrorMessage)
    {
        public bool IsSuccess => Spec != null;
    }
}
