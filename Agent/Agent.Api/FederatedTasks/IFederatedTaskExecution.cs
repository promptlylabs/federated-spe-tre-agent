using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Runs one federated task against its dataset. The polling job does everything around it: the input checks
    /// before, marking the task running, and saving and releasing the result after.
    /// </summary>
    /// <remarks>
    /// An implementation returns a result document or an error with a <see cref="FederatedTaskErrorCodes"/> code.
    /// It lets a <see cref="DatasetStoreException"/> propagate so the polling job retries the task at the next poll;
    /// any other exception is reported as an internal error.
    /// </remarks>
    public interface IFederatedTaskExecution
    {
        /// <param name="task">The task as polled from Submission.</param>
        /// <param name="spec">The task parsed by <see cref="FederatedTesProfile"/> and addressed to this TRE.</param>
        /// <param name="fileKey">The dataset file the input checks found, as a key in the dataset bucket.</param>
        Task<ExecutionResult> RunAsync(TesTask task, FederatedTaskSpec spec, string fileKey, CancellationToken ct);
    }
}
