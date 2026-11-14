using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Body of ReportProgress: the phase the TRE Agent says the task has reached.</summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedTaskProgressReport
    {
        [JsonProperty("task_id")]
        public string? TaskId { get; set; }

        [JsonProperty("phase")]
        public string? Phase { get; set; }
    }
}
