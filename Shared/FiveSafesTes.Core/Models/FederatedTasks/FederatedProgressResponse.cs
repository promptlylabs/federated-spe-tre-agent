using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Response to an accepted progress report: the step the task is at now.</summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedProgressResponse
    {
        [JsonProperty("step")]
        public string Step { get; set; } = "";
    }
}
