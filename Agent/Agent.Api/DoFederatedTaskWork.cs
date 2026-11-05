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
        private readonly IResultOutputStore _outputs;
        private readonly IConfiguration _config;
        private readonly TimeProvider _clock;

        public DoFederatedTaskWork(IFederatedTaskClient client, IDatasetReader reader, IResultOutputStore outputs,
            IConfiguration config, TimeProvider clock)
        {
            _client = client;
            _reader = reader;
            _outputs = outputs;
            _config = config;
            _clock = clock;
        }

        public async Task Execute()
        {
            // Don't execute if no credentials have been uploaded.
            if (!_client.IsConfigured)
            {
                Log.Warning("Federated tasks need the TRE service account (ConfigInputMethod Upload)");
                return;
            }

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
                try
                {
                    await ProcessAsync(task, treName.Trim());
                }
                catch (Exception ex)
                {
                    // A task whose save or report failed stays QUEUED on the server and is retried on the next poll.
                    Log.Error(ex, "Federated tasks: task {TaskId} could not be completed this poll; it stays QUEUED", task.Id);
                }
            }
        }

        /// <summary>
        /// Compute, save, release and verify one task. Throws on storage or report transport failure.
        /// </summary>
        private async Task ProcessAsync(TesTask task, string treName)
        {
            var taskId = task.Id ?? string.Empty;

            // The id becomes an object key; only a GUID is accepted, and it is stored in its canonical form.
            if (!Guid.TryParse(taskId, out var guid))
            {
                await ReportErrorAsync(Error(taskId, FederatedTaskErrorCodes.InvalidTask, "the task id is not a valid GUID"));
                return;
            }
            var storeKey = guid.ToString("D");

            // A file saved by an earlier poll is released as it is; the result is never computed twice.
            var bytes = await _outputs.TryGetAsync(storeKey, CancellationToken.None);

            if (bytes == null)
            {
                var computed = await ComputeAsync(task, taskId, treName);
                if (computed.Skip) return;
                if (computed.ErrorReport != null)
                {
                    await ReportErrorAsync(computed.ErrorReport);
                    return;
                }

                bytes = FederatedResultDocumentJson.Serialize(computed.Document!);
                if (bytes.Length > FederatedResultLimits.MaxResultBytes)
                {
                    await ReportErrorAsync(Error(taskId, FederatedTaskErrorCodes.ResultTooLarge,
                        "the result file is larger than the size limit", computed.Document!.DatasetSha256));
                    return;
                }

                await _outputs.SaveAsync(storeKey, bytes, CancellationToken.None);
            }

            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
            Log.Information("Federated tasks: saved result for {TaskId} sha256 {Sha}", taskId, sha);

            // Egress review will be inserted here, between save and release.

            var outcome = await _client.ReportAsync(
                new FederatedTaskResultReport { TaskId = taskId, State = TesState.COMPLETEEnum }, bytes);
            await HandleOutcomeAsync(taskId, storeKey, sha, outcome);
        }

        /// <summary>Reports an error state (no file) and logs when Submission does not accept it.</summary>
        private async Task ReportErrorAsync(FederatedTaskResultReport report)
        {
            var outcome = await _client.ReportAsync(report, null);
            if (!outcome.Accepted)
            {
                Log.Warning("Federated tasks: Submission did not accept the error report for task {TaskId} (status {Status}, {ErrorCode}); it stays QUEUED",
                    report.TaskId, outcome.StatusCode, outcome.ErrorCode);
            }
        }

        private async Task HandleOutcomeAsync(string taskId, string storeKey, string sha, FederatedReportOutcome outcome)
        {
            if (outcome.StatusCode == 200 || (outcome.StatusCode == 409 && outcome.ResultSha256 != null))
            {
                await VerifyAndMarkReleasedAsync(taskId, storeKey, sha, outcome);
                return;
            }

            if (outcome.StatusCode == 400 &&
                outcome.ErrorCode is FederatedTaskErrorCodes.InvalidResult or FederatedTaskErrorCodes.ResultTooLarge)
            {
                Log.Warning("Federated tasks: Submission rejected the result for task {TaskId} with {ErrorCode}", taskId, outcome.ErrorCode);
                await ReportErrorAsync(Error(taskId, outcome.ErrorCode!, outcome.ErrorMessage));
                return;
            }

            Log.Warning("Federated tasks: Submission did not accept the result for task {TaskId} (status {Status}, {ErrorCode}); it stays QUEUED",
                taskId, outcome.StatusCode, outcome.ErrorCode);
        }

        private async Task VerifyAndMarkReleasedAsync(string taskId, string storeKey, string sha, FederatedReportOutcome outcome)
        {
            if (outcome.ResultKey == null || outcome.ResultSha256 == null || outcome.ResultSha256 != sha)
            {
                Log.Error("Federated tasks: released sha256 mismatch for task {TaskId}: saved {Saved}, Submission stored {Released}",
                    taskId, sha, outcome.ResultSha256);
                return;
            }

            try
            {
                await _outputs.MarkReleasedAsync(storeKey, outcome.ResultKey, sha, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Federated tasks: result for task {TaskId} was released but could not be marked in the TRE bucket", taskId);
            }
        }

        private sealed record Computed(FederatedResultDocument? Document, FederatedTaskResultReport? ErrorReport, bool Skip);

        /// <summary>
        /// Runs one task against its dataset. Returns a document, an error report, or Skip when the task must stay
        /// QUEUED without a report. Does not touch result storage.
        /// </summary>
        private async Task<Computed> ComputeAsync(TesTask task, string taskId, string treName)
        {
            try
            {
                // Enqueue refuses tasks outside the profile, so a failure here is defence in depth.
                var parsed = FederatedTesProfile.Parse(task);
                if (!parsed.IsSuccess)
                    return new Computed(null, Error(taskId, parsed.ErrorCode!, parsed.ErrorMessage), false);

                // Tasks are polled per TRE, so a mismatch means this agent's TreName disagrees with the TRE's name
                // in Submission. Don't read data, and don't report: the task stays QUEUED and runs once config is fixed.
                if (!string.Equals(parsed.Spec!.TreName, treName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("Federated tasks: task {TaskId} is for TRE '{TaskTre}' but this agent's TreName is '{TreName}'; not running it",
                        taskId, parsed.Spec.TreName, treName);
                    return new Computed(null, null, true);
                }

                var dataset = await _reader.ReadAsync(parsed.Spec.Dataset, CancellationToken.None);
                if (!dataset.IsSuccess)
                    return new Computed(null, Error(taskId, dataset.ErrorCode!, dataset.ErrorMessage, dataset.Sha256), false);

                var result = FederatedOperationExecutor.Execute(parsed.Spec, dataset.Table!);
                if (!result.IsSuccess)
                    return new Computed(null, Error(taskId, result.ErrorCode!, result.ErrorMessage, dataset.Sha256), false);

                var document = new FederatedResultDocument
                {
                    TaskId = taskId,
                    Tre = parsed.Spec.TreName,
                    StudyId = parsed.Spec.StudyId,
                    Operation = parsed.Spec.Operation,
                    Dataset = parsed.Spec.Dataset,
                    DatasetSha256 = dataset.Sha256,
                    CompletedAt = _clock.GetUtcNow(),
                    Result = new FederatedScalarResult { Value = result.Value, RowCount = result.RowCount }
                };
                return new Computed(document, null, false);
            }
            catch (Exception ex)
            {
                // The dataset reader lets store failures propagate; report them rather than fail the whole poll.
                // The exception can name buckets, endpoints or hosts, so it stays in the TRE's own log.
                Log.Error(ex, "Federated tasks: task {TaskId} failed unexpectedly", taskId);
                return new Computed(null, new FederatedTaskResultReport
                {
                    TaskId = taskId,
                    State = TesState.SYSTEMERROREnum,
                    ErrorCode = FederatedTaskErrorCodes.InternalError,
                    ErrorMessage = "an internal error occurred in the TRE; the TRE admin can find the details in the Agent log"
                }, false);
            }
        }

        // Task-spec values are echoed into messages; Submission rejects control characters.
        private static string CleanControlChars(string message) =>
            string.Create(message.Length, message, (span, m) =>
            {
                for (var i = 0; i < m.Length; i++) span[i] = char.IsControl(m[i]) ? ' ' : m[i];
            });

        private static FederatedTaskResultReport Error(string taskId, string code, string? message, string? datasetSha256 = null)
        {
            Log.Information("Federated tasks: task {TaskId} failed with {ErrorCode}", taskId, code);
            return new FederatedTaskResultReport
            {
                TaskId = taskId,
                State = TesState.EXECUTORERROREnum,
                ErrorCode = code,
                ErrorMessage = message == null ? null : FederatedResultLimits.TruncateErrorMessage(CleanControlChars(message)),
                DatasetSha256 = datasetSha256
            };
        }
    }
}
