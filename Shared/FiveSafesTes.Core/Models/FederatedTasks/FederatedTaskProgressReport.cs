using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Body of ReportProgress: the step the TRE Agent says the task has reached.</summary>
    // IsReference = false: the global PreserveReferencesHandling would otherwise add a "$id" key.
    [JsonObject(IsReference = false)]
    public sealed class FederatedTaskProgressReport
    {
        [JsonProperty("task_id")]
        public string? TaskId { get; set; }

        [JsonProperty("step")]
        public string? Step { get; set; }
    }
}
