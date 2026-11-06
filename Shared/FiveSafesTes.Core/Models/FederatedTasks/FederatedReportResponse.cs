using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// Response to an accepted report. The location fields are set only for a COMPLETE report.
    /// </summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedReportResponse
    {
        [JsonProperty("result")]
        public bool Result { get; set; }

        [JsonProperty("result_bucket", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResultBucket { get; set; }

        [JsonProperty("result_key", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResultKey { get; set; }

        [JsonProperty("result_sha256", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResultSha256 { get; set; }
    }
}
