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
    }
}
