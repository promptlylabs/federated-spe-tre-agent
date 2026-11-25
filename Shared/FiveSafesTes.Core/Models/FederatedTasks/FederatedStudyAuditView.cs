using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// How a study version's result was produced, for admins: who submitted what, each hospital's phases with their times
    /// and codes, each released result's hash, the pooling and the pooled result's hash, or why there is none. Reviewer
    /// names are not here: they stay in each hospital's review record. Plain JSON, not TES.
    /// </summary>
    [JsonObject(IsReference = false)]
    public sealed class FederatedStudyAuditView
    {
        [JsonProperty("study_id")] public string StudyId { get; set; } = "";
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("version_id")] public Guid VersionId { get; set; }
        [JsonProperty("submitted_by")] public string? SubmittedBy { get; set; }
        [JsonProperty("submitted_at")] public DateTime SubmittedAt { get; set; }
        [JsonProperty("code_sha256")] public string CodeSha256 { get; set; } = "";
        [JsonProperty("deadline_at")] public DateTime DeadlineAt { get; set; }
        [JsonProperty("outcome")] public string? Outcome { get; set; }
        [JsonProperty("outcome_reason")] public string? OutcomeReason { get; set; }
        [JsonProperty("outcome_message")] public string? OutcomeMessage { get; set; }
        [JsonProperty("outcome_at")] public DateTime? OutcomeAt { get; set; }
        [JsonProperty("ready_at")] public DateTime? ReadyAt { get; set; }
        [JsonProperty("aggregation_started_at")] public DateTime? AggregationStartedAt { get; set; }
        [JsonProperty("aggregation_error_code")] public string? AggregationErrorCode { get; set; }

        /// <summary>Null when there is no pooled result; outcome and outcome_reason say why.</summary>
        [JsonProperty("pooled_result_sha256")] public string? PooledResultSha256 { get; set; }

        [JsonProperty("hospitals")] public List<FederatedHospitalAudit> Hospitals { get; set; } = [];
    }

    [JsonObject(IsReference = false)]
    public sealed class FederatedHospitalAudit
    {
        [JsonProperty("tre")] public string Tre { get; set; } = "";
        [JsonProperty("task_id")] public Guid TaskId { get; set; }
        [JsonProperty("phases")] public List<FederatedPhaseAudit> Phases { get; set; } = [];

        /// <summary>The hash of the hospital's released result file, when it released one.</summary>
        [JsonProperty("result_sha256")] public string? ResultSha256 { get; set; }
    }

    [JsonObject(IsReference = false)]
    public sealed class FederatedPhaseAudit
    {
        [JsonProperty("phase")] public string Phase { get; set; } = "";
        [JsonProperty("at")] public DateTime At { get; set; }
        [JsonProperty("error_code", NullValueHandling = NullValueHandling.Ignore)] public string? ErrorCode { get; set; }
    }
}
