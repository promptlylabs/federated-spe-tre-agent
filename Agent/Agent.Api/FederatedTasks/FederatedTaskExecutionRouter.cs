using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Sends a code task to <see cref="CodeExecution"/> and a count or mean task to <see cref="OperationExecution"/>.
    /// The members keyed by task id go to the code runner: only code tasks have runs that outlive a poll.
    /// </summary>
    public sealed class FederatedTaskExecutionRouter : IFederatedTaskExecution
    {
        private readonly OperationExecution _operations;
        private readonly CodeExecution _code;

        public FederatedTaskExecutionRouter(OperationExecution operations, CodeExecution code)
        {
            _operations = operations;
            _code = code;
        }

        public Task<bool> HasRunAsync(string taskId, CancellationToken ct) => _code.HasRunAsync(taskId, ct);

        public Task<bool> CanStartAsync(FederatedTaskSpec spec, CancellationToken ct) =>
            spec.IsCodeTask ? _code.CanStartAsync(spec, ct) : _operations.CanStartAsync(spec, ct);

        public Task<ExecutionResult> RunAsync(TesTask task, FederatedTaskSpec spec, string? fileKey, CancellationToken ct) =>
            spec.IsCodeTask ? _code.RunAsync(task, spec, fileKey, ct) : _operations.RunAsync(task, spec, fileKey, ct);

        public Task RemoveRunAsync(string taskId, CancellationToken ct) => _code.RemoveRunAsync(taskId, ct);

        public Task SweepAsync(IReadOnlyCollection<string> pendingTaskIds, CancellationToken ct) =>
            _code.SweepAsync(pendingTaskIds, ct);
    }
}
