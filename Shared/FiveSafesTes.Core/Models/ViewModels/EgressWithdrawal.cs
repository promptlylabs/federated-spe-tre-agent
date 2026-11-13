namespace FiveSafesTes.Core.Models.ViewModels
{
    /// <summary>The TRE Agent asks the Egress app to close a federated request that can no longer be decided.</summary>
    public class EgressWithdrawal
    {
        public string SubmissionId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
