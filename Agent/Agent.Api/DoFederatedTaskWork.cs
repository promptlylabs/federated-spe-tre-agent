using Agent.Api.FederatedTasks;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Agent.Api
{
    public interface IDoFederatedTaskWork
    {
        [AutomaticRetry(Attempts = 0)]
        [DisableConcurrentExecution(timeoutInSeconds: 300)]
        Task Execute();
    }

    /// <summary>
    /// Polls Submission for pending federated tasks, runs each against its dataset and reports the result.
    /// </summary>
    public class DoFederatedTaskWork : IDoFederatedTaskWork
    {
        private readonly IFederatedTaskClient _client;
        private readonly IDatasetReader _reader;
        private readonly IConfiguration _config;

        public DoFederatedTaskWork(IFederatedTaskClient client, IDatasetReader reader, IConfiguration config)
        {
            _client = client;
            _reader = reader;
            _config = config;
        }

        public async Task Execute()
        {
            // Don't execute if no credentials have been uploaded.
            if (!_client.IsConfigured) return;

            // Without its own name the agent cannot tell which tasks are addressed to it; leave them QUEUED.
            var treName = _config["TreName"];
            if (string.IsNullOrWhiteSpace(treName))
            {
                Log.Error("Federated tasks: TreName is not configured, not running any task");
                return;
            }

            var pending = await _client.GetPendingAsync() ?? new List<TesTask>();
            if (pending.Count == 0) return;

            Log.Information("Federated tasks: {Count} pending task(s)", pending.Count);

            foreach (var task in pending)
            {
                var report = await RunAsync(task, treName.Trim());
                if (report == null) continue;
                try
                {
                    // A task whose report is lost stays QUEUED on the server and is retried on the next poll.
                    if (!await _client.ReportAsync(report))
                    {
                        Log.Warning("Federated tasks: Submission did not accept the result for task {TaskId}", report.TaskId);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Federated tasks: failed to report the result for task {TaskId}", report.TaskId);
                }
            }
        }

        /// <summary>
        /// Runs one task and returns its result, or null when the task must stay QUEUED without a report.
        /// </summary>
        private async Task<FederatedTaskResultReport?> RunAsync(TesTask task, string treName)
        {
            var taskId = task.Id ?? string.Empty;
            try
            {
                // Enqueue refuses tasks outside the profile, so a failure here is defence in depth.
                var parsed = FederatedTesProfile.Parse(task);
                if (!parsed.IsSuccess)
                    return Error(taskId, parsed.ErrorCode!, parsed.ErrorMessage);

                // Tasks are polled per TRE, so a mismatch means this agent's TreName disagrees with the TRE's name
                // in Submission. Don't read data, and don't report: the task stays QUEUED and runs once config is fixed.
                if (!string.Equals(parsed.Spec!.TreName, treName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("Federated tasks: task {TaskId} is for TRE '{TaskTre}' but this agent's TreName is '{TreName}'; not running it",
                        taskId, parsed.Spec.TreName, treName);
                    return null;
                }

                var dataset = await _reader.ReadAsync(parsed.Spec!.Dataset, CancellationToken.None);
                if (!dataset.IsSuccess)
                    return Error(taskId, dataset.ErrorCode!, dataset.ErrorMessage, dataset.Sha256);

                var result = FederatedOperationExecutor.Execute(parsed.Spec, dataset.Table!);
                if (!result.IsSuccess)
                    return Error(taskId, result.ErrorCode!, result.ErrorMessage, dataset.Sha256);

                return new FederatedTaskResultReport
                {
                    TaskId = taskId,
                    State = TesState.COMPLETEEnum,
                    Value = result.Value,
                    RowCount = result.RowCount,
                    DatasetSha256 = dataset.Sha256
                };
            }
            catch (Exception ex)
            {
                // The dataset reader lets store failures propagate; report them rather than fail the whole poll.
                Log.Error(ex, "Federated tasks: task {TaskId} failed unexpectedly", taskId);
                return new FederatedTaskResultReport
                {
                    TaskId = taskId,
                    State = TesState.SYSTEMERROREnum,
                    ErrorCode = "internal_error",
                    ErrorMessage = ex.Message
                };
            }
        }

        private static FederatedTaskResultReport Error(string taskId, string code, string? message, string? datasetSha256 = null)
        {
            Log.Information("Federated tasks: task {TaskId} failed with {ErrorCode}", taskId, code);
            return new FederatedTaskResultReport
            {
                TaskId = taskId,
                State = TesState.EXECUTORERROREnum,
                ErrorCode = code,
                ErrorMessage = message,
                DatasetSha256 = datasetSha256
            };
        }
    }
}
