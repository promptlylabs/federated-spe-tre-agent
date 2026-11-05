using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// Body of every 4xx the federated task API returns. The keys match the error keys of a failed task's
    /// <c>logs[0].metadata</c>, so callers read API and task errors the same way.
    /// </summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedApiError
    {
        [JsonProperty("error_code")]
        public string ErrorCode { get; set; } = string.Empty;

        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>On a 409 for a COMPLETE task: the SHA-256 of the stored result, so the Agent can compare.</summary>
        [JsonProperty("result_sha256", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResultSha256 { get; set; }
    }
}
