namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// The JSON document a TRE Agent saves for a completed federated task and uploads to the orchestrator.
    /// </summary>
    public sealed class FederatedResultDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string TaskId { get; set; } = string.Empty;
        public string Tre { get; set; } = string.Empty;
        public string StudyId { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Dataset { get; set; } = string.Empty;

        /// <summary>Lowercase hex SHA-256 of the dataset file the TRE read.</summary>
        public string? DatasetSha256 { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public FederatedScalarResult Result { get; set; } = new();
    }

    /// <summary>A single-number result. Later result kinds are added as new <see cref="Kind"/> values.</summary>
    public sealed class FederatedScalarResult
    {
        public string Kind { get; set; } = "scalar";
        public decimal? Value { get; set; }
        public int RowCount { get; set; }
    }
}
