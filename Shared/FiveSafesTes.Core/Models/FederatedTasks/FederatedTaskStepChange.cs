namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>One entry in a federated task's step history.</summary>
    public class FederatedTaskStepChange
    {
        public int Id { get; set; }
        public int FederatedTaskId { get; set; }
        public FederatedTaskStep Step { get; set; }

        /// <summary>UTC.</summary>
        public DateTime At { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
