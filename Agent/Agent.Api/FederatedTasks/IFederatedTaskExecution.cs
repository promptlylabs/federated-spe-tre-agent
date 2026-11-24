using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Runs one federated task against its dataset. The polling job does everything around it: the input checks
    /// before, marking the task running, and saving and releasing the result after.
    /// </summary>
    /// <remarks>
    /// A run of researcher code outlives the poll that starts it: each poll moves it one step (start, check, collect)
    /// and the run itself is the record of where it is, so the members that take a task id find it again.
    /// <para>
    /// <see cref="RunAsync"/> returns a result document, an error with a <see cref="FederatedTaskErrorCodes"/> code, or
    /// in progress. It lets a <see cref="DatasetStoreException"/> propagate so the polling job retries the task at the
    /// next poll; any other exception is reported as an internal error.
    /// </para>
    /// </remarks>
    public interface IFederatedTaskExecution
    {
        /// <summary>True when a run exists for the task (it was started by an earlier poll).</summary>
        /// <param name="taskId">The task id in its canonical form, as used for the result's object key.</param>
        Task<bool> HasRunAsync(string taskId, CancellationToken ct);

        /// <summary>False when no new run may start now (the TRE's limit on runs at the same time is reached).</summary>
        Task<bool> CanStartAsync(FederatedTaskSpec spec, CancellationToken ct);

        /// <param name="task">The task as polled from Submission.</param>
        /// <param name="spec">The task parsed by <see cref="FederatedTesProfile"/> and addressed to this TRE.</param>
        /// <param name="fileKey">
        /// The dataset file the input checks found, as a key in the dataset bucket; null when a run exists and is
        /// only checked.
        /// </param>
        Task<ExecutionResult> RunAsync(TesTask task, FederatedTaskSpec spec, string? fileKey, CancellationToken ct);

        /// <summary>Stops and removes the task's run, if there is one. Safe to repeat.</summary>
        /// <param name="taskId">The task id in its canonical form.</param>
        Task RemoveRunAsync(string taskId, CancellationToken ct);

        /// <summary>Removes the runs that are provably finished, and staged data left without a run.</summary>
        /// <param name="pendingTaskIds">The canonical ids of the tasks this poll was handed.</param>
        Task SweepAsync(IReadOnlyCollection<string> pendingTaskIds, CancellationToken ct);
    }
}
