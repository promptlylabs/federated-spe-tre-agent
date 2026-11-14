using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Response to an accepted progress report: the phase the task is at now.</summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedProgressResponse
    {
        [JsonProperty("phase")]
        public string Phase { get; set; } = "";
    }
}
