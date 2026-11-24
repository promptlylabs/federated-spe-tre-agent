using FiveSafesTes.Core.Models.FederatedTasks;

namespace Submission.Api.Services
{
    /// <summary>Hands a hospital's part of a study version to that hospital once the part is stored.</summary>
    public interface IFederatedTaskDispatch
    {
        Task SendAsync(FederatedTask part, CancellationToken ct);
    }

    /// <summary>
    /// Does nothing: the part is already queued in the database, and the hospital's Agent pulls it with
    /// GetPendingTasksForTre.
    /// </summary>
    public sealed class AgentPollDispatch : IFederatedTaskDispatch
    {
        public Task SendAsync(FederatedTask part, CancellationToken ct) => Task.CompletedTask;
    }
}
