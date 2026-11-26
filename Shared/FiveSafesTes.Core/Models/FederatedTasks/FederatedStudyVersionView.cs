using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>One submission of a study and how each hospital is getting on with it. Never carries result content.</summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedStudyVersionView
    {
        [JsonProperty("study_id")]
        public string StudyId { get; set; } = "";

        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("version_id")]
        public Guid VersionId { get; set; }

        [JsonProperty("submitted_at")]
        public DateTime SubmittedAt { get; set; }

        [JsonProperty("code_sha256")]
        public string CodeSha256 { get; set; } = "";

        /// <summary>open, canceling or ended.</summary>
        [JsonProperty("state")]
        public string State { get; set; } = "";

        [JsonProperty("canceled_at")]
        public DateTime? CanceledAt { get; set; }

        /// <summary>Every version number of the study, ascending. Only set by GetStudy.</summary>
        [JsonProperty("versions", NullValueHandling = NullValueHandling.Ignore)]
        public int[]? Versions { get; set; }

        [JsonProperty("hospitals")]
        public List<FederatedHospitalView> Hospitals { get; set; } = [];
    }

    [JsonObject(IsReference = false)]
    public sealed class FederatedHospitalView
    {
        [JsonProperty("tre")]
        public string Tre { get; set; } = "";

        [JsonProperty("task_id")]
        public Guid TaskId { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; } = "";

        /// <summary>The TES state.</summary>
        [JsonProperty("state")]
        public TesState State { get; set; }

        [JsonProperty("times", IsReference = false)]
        public Dictionary<string, string> Times { get; set; } = [];

        [JsonProperty("error_code", NullValueHandling = NullValueHandling.Ignore)]
        public string? ErrorCode { get; set; }

        [JsonProperty("result_sha256", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResultSha256 { get; set; }
    }
}
