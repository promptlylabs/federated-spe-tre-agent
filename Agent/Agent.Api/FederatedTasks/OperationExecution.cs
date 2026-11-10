using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Runs the allow-listed count and mean operations in the Agent's own process: reads and parses the dataset, then
    /// computes the value with <see cref="FederatedOperationExecutor"/>.
    /// </summary>
    public sealed class OperationExecution : IFederatedTaskExecution
    {
        private readonly IDatasetReader _reader;
        private readonly TimeProvider _clock;

        public OperationExecution(IDatasetReader reader, TimeProvider clock)
        {
            _reader = reader;
            _clock = clock;
        }

        public async Task<ExecutionResult> RunAsync(TesTask task, FederatedTaskSpec spec, string fileKey, CancellationToken ct)
        {
            // A bucket failure throws DatasetStoreException; the polling job retries it.
            var dataset = await _reader.ReadAsync(fileKey, ct);
            if (!dataset.IsSuccess)
                return ExecutionResult.Fail(dataset.ErrorCode!, dataset.ErrorMessage, dataset.Sha256);

            var result = FederatedOperationExecutor.Execute(spec, dataset.Table!);
            if (!result.IsSuccess)
                return ExecutionResult.Fail(result.ErrorCode!, result.ErrorMessage, dataset.Sha256);

            return ExecutionResult.Ok(new FederatedResultDocument
            {
                TaskId = task.Id ?? string.Empty,
                Tre = spec.TreName,
                StudyId = spec.StudyId,
                Operation = spec.Operation,
                Dataset = spec.Dataset,
                DatasetSha256 = dataset.Sha256,
                CompletedAt = _clock.GetUtcNow(),
                Result = new FederatedScalarResult { Value = result.Value, RowCount = result.RowCount }
            });
        }
    }
}
