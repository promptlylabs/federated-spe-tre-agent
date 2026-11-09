using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Talks to the Submission layer's federated task endpoints as the TRE service account.
    /// </summary>
    public interface IFederatedTaskClient
    {
        /// <summary>
        /// True only when the TRE service account credentials have been uploaded (ConfigInputMethod Upload).
        /// </summary>
        bool IsConfigured { get; }

        Task<List<TesTask>> GetPendingAsync();

        /// <summary>
        /// Sends the report, with the result file for a COMPLETE one. Returns what Submission answered;
        /// throws only on a network or token failure.
        /// </summary>
        Task<FederatedReportOutcome> ReportAsync(FederatedTaskResultReport report, byte[]? resultFile);
    }
}
