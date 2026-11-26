using System.Text.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// The JSON document a TRE Agent saves for a completed code task (operation "run-analysis") and uploads to the
    /// orchestrator. The result is the researcher's dict.
    /// </summary>
    public sealed class FederatedAnalysisResultDocument
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
        public FederatedAnalysisResult Result { get; set; } = new();
    }

    /// <summary>What site_analysis returned: <see cref="Value"/> is its dict, <see cref="N"/> its n.</summary>
    public sealed class FederatedAnalysisResult
    {
        public string Kind { get; set; } = "analysis";
        public long N { get; set; }
        public JsonElement Value { get; set; }
    }
}
