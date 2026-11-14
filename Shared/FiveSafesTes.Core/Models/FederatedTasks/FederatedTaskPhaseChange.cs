namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>One entry in a federated task's phase history.</summary>
    public class FederatedTaskPhaseChange
    {
        public int Id { get; set; }
        public int FederatedTaskId { get; set; }
        public FederatedTaskPhase Phase { get; set; }

        /// <summary>UTC.</summary>
        public DateTime At { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
