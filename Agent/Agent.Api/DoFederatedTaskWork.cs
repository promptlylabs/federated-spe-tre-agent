using Agent.Api.Models;
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
        private readonly FederatedTaskSettings _settings;
        private readonly FederatedInputChecks _checks;
        private readonly AgentStartTime _startedAt;

        public DoFederatedTaskWork(IFederatedTaskClient client, IDatasetReader reader, IResultOutputStore outputs,
            IConfiguration config, TimeProvider clock, FederatedTaskSettings settings, AgentStartTime startedAt)
        {
            _client = client;
            _reader = reader;
            _outputs = outputs;
            _config = config;
            _clock = clock;
            _settings = settings;
            _checks = new FederatedInputChecks(reader, settings);
            _startedAt = startedAt;
        }

        public async Task Execute()
        {
            // Don't execute if no credentials have been uploaded.
            if (!_client.IsConfigured)
            {
                Log.Warning("Federated tasks: the TRE service account is not configured (ConfigInputMethod Upload with ClientId and ClientSecret); not polling");
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
            var held = 0;

            foreach (var task in pending)
            {
                // Waiting for output approval: the TRE has nothing to do until Submission records a decision.
                if (task.State == TesState.PAUSEDEnum) continue;

                // Only QUEUED and RUNNING tasks are the Agent's to work on. Any other state here is unexpected.
                if (task.State is not (TesState.QUEUEDEnum or TesState.RUNNINGEnum))
                {
                    Log.Warning("Federated tasks: task {TaskId} is pending in unexpected state {State}; skipping it", task.Id, task.State);
                    continue;
                }

                try
                {
                    if (await ProcessAsync(task, treName.Trim())) held++;
                }
                catch (Exception ex)
                {
                    // A task whose save or report failed keeps its step on the server and is retried on the next poll.
                    Log.Error(ex, "Federated tasks: task {TaskId} could not be completed this poll; it keeps its step", task.Id);
                }
            }

            // One line per poll, not per task, so a backlog does not flood the log.
            if (held > 0)
                Log.Error("Federated tasks: FederatedTaskSettings:ApprovedImages is not configured; {Count} task(s) were not checked or run and keep their step",
                    held);
        }

        /// <summary>
        /// Compute, save, release and verify one task. Returns true when the input checks held it because the site has
        /// not configured them, so the caller can log that once per poll. Throws on storage or report transport failure.
        /// </summary>
        private async Task<bool> ProcessAsync(TesTask task, string treName)
        {
            var taskId = task.Id ?? string.Empty;

            // The id becomes an object key; only a GUID is accepted, and it is stored in its canonical form.
            if (!Guid.TryParse(taskId, out var guid))
            {
                await ReportErrorAsync(Error(taskId, FederatedTaskErrorCodes.InvalidTask, "the task id is not a valid GUID"));
                return false;
            }
            var storeKey = guid.ToString("D");

            // A file saved by an earlier poll is released as it is; the result is never computed twice.
            var bytes = await _outputs.TryGetAsync(storeKey, CancellationToken.None);

            if (bytes == null)
            {
                var prepared = Prepare(task, taskId, treName);
                if (prepared.Skip) return false;
                if (prepared.ErrorReport != null)
                {
                    await ReportErrorAsync(prepared.ErrorReport);
                    return false;
                }

                // The hospital's input checks run before the task is marked running and before any data is read.
                // A task that fails one is rejected; one the TRE cannot judge now keeps its step.
                InputCheckOutcome checkOutcome;
                try
                {
                    checkOutcome = await _checks.RunAsync(task, prepared.Spec!, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await RetryOrGiveUpAsync(task, taskId, ex, "listed");
                    return false;
                }

                if (checkOutcome.Kind == InputCheckKind.Held) return true;
                if (checkOutcome.Kind == InputCheckKind.Rejected)
                {
                    await ReportErrorAsync(Error(taskId, checkOutcome.ErrorCode!, checkOutcome.ErrorMessage));
                    return false;
                }

                // A QUEUED task is marked running before its data is read. A RUNNING one was marked by an earlier
                // poll and is resumed. A transport failure here throws and leaves the task for the next poll.
                if (task.State == TesState.QUEUEDEnum && !await ReportRunningAsync(taskId))
                    return false;

                // The task is running now. If the bucket fails during the read, it stays running and is resumed at the
                // next poll, under the same time limit as a failed listing. Any other fault cannot be fixed by
                // waiting, so it is reported at once.
                DatasetReadResult dataset;
                try
                {
                    dataset = await _reader.ReadAsync(checkOutcome.FileKey!, CancellationToken.None);
                }
                catch (DatasetStoreException ex)
                {
                    await RetryOrGiveUpAsync(task, taskId, ex, "read");
                    return false;
                }
                catch (Exception ex)
                {
                    await ReportErrorAsync(InternalError(taskId, ex));
                    return false;
                }

                var computed = Compute(prepared.Spec!, dataset, taskId);
                if (computed.ErrorReport != null)
                {
                    await ReportErrorAsync(computed.ErrorReport);
                    return false;
                }

                bytes = FederatedResultDocumentJson.Serialize(computed.Document!);
                await _outputs.SaveAsync(storeKey, bytes, CancellationToken.None);
            }
            else
            {
                // A saved result passed the input checks on an earlier poll, so they don't run again: releasing it is
                // output approval's decision. If the site has since removed the task's image, say so in the log.
                var image = FederatedInputChecks.ImageOf(task);
                if (_checks.IsConfigured && !_checks.IsImageApproved(image))
                    Log.Warning("Federated tasks: releasing the saved result of task {TaskId}, although its image {Image} is no longer approved at this TRE; the input checks passed before the result was saved",
                        taskId, image);
            }

            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
            Log.Information("Federated tasks: saved result for {TaskId} sha256 {Sha}", taskId, sha);

            // Egress review will be inserted here, between save and release.

            var outcome = await _client.ReportAsync(
                new FederatedTaskResultReport { TaskId = taskId, State = TesState.COMPLETEEnum }, bytes);
            await HandleOutcomeAsync(taskId, storeKey, sha, bytes, outcome);
            return false;
        }

        /// <summary>Reports an error state (no file) and logs when Submission does not accept it.</summary>
        private async Task ReportErrorAsync(FederatedTaskResultReport report)
        {
            var outcome = await _client.ReportAsync(report, null);
            if (IsAlreadyFinal(outcome))
            {
                Log.Warning("Federated tasks: task {TaskId} was already reported to Submission; the error report was not recorded", report.TaskId);
            }
            else if (!outcome.Accepted)
            {
                Log.Warning("Federated tasks: Submission did not accept the error report for task {TaskId} (status {Status}, {ErrorCode}); it keeps its step",
                    report.TaskId, outcome.StatusCode, outcome.ErrorCode);
            }
        }

        private async Task HandleOutcomeAsync(string taskId, string storeKey, string sha, byte[] bytes, FederatedReportOutcome outcome)
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
                await ReportErrorAsync(Error(taskId, outcome.ErrorCode!, outcome.ErrorMessage, DatasetShaFrom(bytes)));
                return;
            }

            if (IsAlreadyFinal(outcome))
            {
                // The task is already terminal on the server (an error state, or a COMPLETE without a stored sha).
                Log.Warning("Federated tasks: task {TaskId} was already reported to Submission and carries no result sha256; nothing more to do", taskId);
                return;
            }

            Log.Warning("Federated tasks: Submission did not accept the result for task {TaskId} (status {Status}, {ErrorCode}); it keeps its step",
                taskId, outcome.StatusCode, outcome.ErrorCode);
        }

        private async Task VerifyAndMarkReleasedAsync(string taskId, string storeKey, string sha, FederatedReportOutcome outcome)
        {
            if (outcome.ResultKey == null || outcome.ResultSha256 == null)
            {
                Log.Warning("Federated tasks: Submission's answer for task {TaskId} had no result key or sha256; the TRE copy is not tagged", taskId);
                return;
            }

            if (outcome.ResultSha256 != sha)
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

        private static readonly System.Text.RegularExpressions.Regex Sha256Hex =
            new(@"^[0-9a-f]{64}\z",System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Best effort: the dataset hash recorded in the saved result document, or null.</summary>
        private static string? DatasetShaFrom(byte[] bytes)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("datasetSha256", out var p) &&
                    p.ValueKind == System.Text.Json.JsonValueKind.String &&
                    p.GetString() is { } s && Sha256Hex.IsMatch(s))
                    return s;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            return null;
        }

        private sealed record Computed(FederatedResultDocument? Document, FederatedTaskResultReport? ErrorReport);

        private sealed record Prepared(FederatedTaskSpec? Spec, FederatedTaskResultReport? ErrorReport, bool Skip);

        /// <summary>
        /// Parses the task and checks it is addressed to this TRE. Returns the spec, an error report, or Skip when
        /// the task must be left alone without a report.
        /// </summary>
        private static Prepared Prepare(TesTask task, string taskId, string treName)
        {
            // Enqueue refuses tasks outside the profile, so a failure here is defence in depth.
            var parsed = FederatedTesProfile.Parse(task);
            if (!parsed.IsSuccess)
                return new Prepared(null, Error(taskId, parsed.ErrorCode!, parsed.ErrorMessage), false);

            // Tasks are polled per TRE, so a mismatch means this agent's TreName disagrees with the TRE's name
            // in Submission. Don't read data, and don't report: the task stays as it is and runs once config is fixed.
            if (!string.Equals(parsed.Spec!.TreName, treName, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("Federated tasks: task {TaskId} is for TRE '{TaskTre}' but this agent's TreName is '{TreName}'; not running it",
                    taskId, parsed.Spec.TreName, treName);
                return new Prepared(null, null, true);
            }

            return new Prepared(parsed.Spec, null, false);
        }

        /// <summary>
        /// Tells Submission the task is running. Returns false when the task must not run now: Submission says it
        /// is already past that step or did not accept the report. Throws on transport failure.
        /// </summary>
        private async Task<bool> ReportRunningAsync(string taskId)
        {
            var outcome = await _client.ReportProgressAsync(taskId, FederatedTaskStep.Running);
            if (outcome.Accepted) return true;

            if (IsAlreadyFinal(outcome))
                Log.Warning("Federated tasks: task {TaskId} is already past the running step on Submission ({ErrorCode}); not running it",
                    taskId, outcome.ErrorCode);
            else
                Log.Warning("Federated tasks: Submission did not accept the running report for task {TaskId} (status {Status}, {ErrorCode}); it will be retried on the next poll",
                    taskId, outcome.StatusCode, outcome.ErrorCode);
            return false;
        }

        /// <summary>
        /// Runs one task against its parsed dataset. Returns a document or an error report. Does not touch storage.
        /// </summary>
        private Computed Compute(FederatedTaskSpec spec, DatasetReadResult dataset, string taskId)
        {
            try
            {
                if (!dataset.IsSuccess)
                    return new Computed(null, Error(taskId, dataset.ErrorCode!, dataset.ErrorMessage, dataset.Sha256));

                var result = FederatedOperationExecutor.Execute(spec, dataset.Table!);
                if (!result.IsSuccess)
                    return new Computed(null, Error(taskId, result.ErrorCode!, result.ErrorMessage, dataset.Sha256));

                var document = new FederatedResultDocument
                {
                    TaskId = taskId,
                    Tre = spec.TreName,
                    StudyId = spec.StudyId,
                    Operation = spec.Operation,
                    Dataset = spec.Dataset,
                    DatasetSha256 = dataset.Sha256,
                    CompletedAt = _clock.GetUtcNow(),
                    Result = new FederatedScalarResult { Value = result.Value, RowCount = result.RowCount }
                };
                return new Computed(document, null);
            }
            catch (Exception ex)
            {
                return new Computed(null, InternalError(taskId, ex));
            }
        }

        /// <summary>
        /// A dataset store failure is retried at the next poll, but not for ever: a bucket that is wrongly configured
        /// or denied would otherwise leave the task unfinished with nothing the orchestrator can see.
        /// </summary>
        private async Task RetryOrGiveUpAsync(TesTask task, string taskId, Exception ex, string action)
        {
            if (!HasWaitedTooLong(task))
            {
                Log.Warning(ex, "Federated tasks: the dataset bucket could not be {Action} for task {TaskId}; it keeps its step and is tried again at the next poll",
                    action, taskId);
                return;
            }

            await ReportErrorAsync(InternalError(taskId, ex));
        }

        /// <summary>
        /// The dataset reader lets store failures propagate; they are reported rather than failing the whole poll.
        /// The exception can name buckets, endpoints or hosts, so it stays in the TRE's own log.
        /// </summary>
        private static FederatedTaskResultReport InternalError(string taskId, Exception ex)
        {
            Log.Error(ex, "Federated tasks: task {TaskId} failed unexpectedly", taskId);
            return new FederatedTaskResultReport
            {
                TaskId = taskId,
                State = TesState.SYSTEMERROREnum,
                ErrorCode = FederatedTaskErrorCodes.InternalError,
                ErrorMessage = "an internal error occurred in the TRE; the TRE admin can find the details in the Agent log"
            };
        }

        /// <summary>
        /// True when DatasetUnreachableGiveUpMinutes have passed since the later of enqueue and Agent start. A task
        /// without a creation time cannot be aged, so it is not retried.
        /// </summary>
        private bool HasWaitedTooLong(TesTask task)
        {
            if (task.CreationTime is not { } created) return true;

            // Count from the later of enqueue and Agent start. A task held while ApprovedImages was missing (setting it
            // means a restart), or queued while the Agent was down, gets the full window once this Agent can try it.
            var since = created > _startedAt.Value ? created : _startedAt.Value;
            return _clock.GetUtcNow() - since >= TimeSpan.FromMinutes(_settings.DatasetUnreachableGiveUpMinutes);
        }

        /// <summary>A 409 that says the task is already final on Submission; task_busy means "send it again".</summary>
        private static bool IsAlreadyFinal(FederatedReportOutcome outcome) =>
            outcome.StatusCode == 409 && outcome.ErrorCode != FederatedTaskErrorCodes.TaskBusy;

        // Task-spec values are echoed into messages; Submission rejects control characters.
        private static string CleanControlChars(string message) =>
            string.Create(message.Length, message, (span, m) =>
            {
                for (var i = 0; i < m.Length; i++) span[i] = char.IsControl(m[i]) ? ' ' : m[i];
            });

        private static FederatedTaskResultReport Error(string taskId, string code, string? message, string? datasetSha256 = null)
        {
            Log.Information("Federated tasks: task {TaskId} failed with {ErrorCode}", taskId, code);
            // An input-check failure is a rejection (CANCELED), not an execution failure.
            var rejected = FederatedTaskErrorCodes.InputCheckCodes.Contains(code);
            return new FederatedTaskResultReport
            {
                TaskId = taskId,
                State = rejected ? TesState.CANCELEDEnum : TesState.EXECUTORERROREnum,
                RejectedAt = rejected ? FederatedRejectedAt.InputChecks : null,
                ErrorCode = code,
                ErrorMessage = message == null ? null : FederatedResultLimits.TruncateErrorMessage(CleanControlChars(message)),
                DatasetSha256 = datasetSha256
            };
        }
    }
}
