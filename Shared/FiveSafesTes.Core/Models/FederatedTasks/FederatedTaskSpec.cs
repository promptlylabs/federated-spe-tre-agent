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
        IReadOnlyList<string> Sites,
        string CodeSha256)
    {
        /// <summary>
        /// The one hospital of a single-site part. Throws for a list of hospitals: only a part addressed to one
        /// hospital has a TRE name.
        /// </summary>
        public string TreName => Sites.Count == 1
            ? Sites[0]
            : throw new InvalidOperationException("TreName is only valid on a task for exactly one hospital");

        /// <summary>The round of the study version this task belongs to (<c>fed.round</c>). Always 1 in the PoC.</summary>
        public int Round { get; init; } = 1;
    }

    /// <summary>
    /// Outcome of parsing a TES task against the federated profile: either a spec or an error code and message.
    /// </summary>
    public sealed record FederatedTaskParseResult(FederatedTaskSpec? Spec, string? ErrorCode, string? ErrorMessage)
    {
        public bool IsSuccess => Spec != null;
    }
}
