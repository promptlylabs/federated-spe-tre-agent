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

        /// <summary>
        /// Releases or rejects one task as soon as its reviewer's decision is recorded, without waiting for the task
        /// to come up in a poll. Retried by Hangfire on a transport failure; the poll acts on the decision too.
        /// </summary>
        Task ActOnDecision(string taskId);
    }

    /// <summary>
    /// Polls Submission for pending federated tasks, runs each against its dataset and reports the result.
    /// </summary>
    public class DoFederatedTaskWork : IDoFederatedTaskWork
    {
        private readonly IFederatedTaskClient _client;
        private readonly IFederatedTaskExecution _execution;
        private readonly IResultOutputStore _outputs;
        private readonly IOutputApproval _approval;
        private readonly IEgressReviewClient _egress;
        private readonly IConfiguration _config;
        private readonly TimeProvider _clock;
        private readonly FederatedTaskSettings _settings;
        private readonly FederatedInputChecks _checks;
        private readonly AgentStartTime _startedAt;

        public DoFederatedTaskWork(IFederatedTaskClient client, IDatasetReader reader, IFederatedTaskExecution execution,
            IResultOutputStore outputs, IOutputApproval approval, IEgressReviewClient egress, IConfiguration config,
            TimeProvider clock, FederatedTaskSettings settings, AgentStartTime startedAt)
        {
            _client = client;
            _execution = execution;
            _outputs = outputs;
            _approval = approval;
            _egress = egress;
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
            var heldForEgress = 0;

            foreach (var task in pending)
            {
                // QUEUED and RUNNING tasks are run; PAUSED ones wait for the hospital's output approval. Any other
                // state here is unexpected.
                if (task.State is not (TesState.QUEUEDEnum or TesState.RUNNINGEnum or TesState.PAUSEDEnum))
                {
                    Log.Warning("Federated tasks: task {TaskId} is pending in unexpected state {State}; skipping it", task.Id, task.State);
                    continue;
                }

                try
                {
                    switch (await ProcessAsync(task, treName.Trim()))
                    {
                        case Hold.InputChecks: held++; break;
                        case Hold.Egress: heldForEgress++; break;
                    }
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
            if (heldForEgress > 0)
                Log.Error("Federated tasks: FederatedEgressSettings BaseUrl/ClientId/ClientSecret are not configured; {Count} result(s) are held for output approval and not released",
                    heldForEgress);
        }

        /// <summary>Why a task was left without a report because the site has not configured something.</summary>
        private enum Hold
        {
            None,
            InputChecks,
            Egress
        }

        /// <summary>
        /// Compute, save, submit for output approval, then release, reject or verify one task. Returns which
        /// unconfigured setting held it, so the caller can log that once per poll. Throws on storage, approval or
        /// report transport failure.
        /// </summary>
        private async Task<Hold> ProcessAsync(TesTask task, string treName)
        {
            var taskId = task.Id ?? string.Empty;

            // The id becomes an object key; only a GUID is accepted, and it is stored in its canonical form.
            if (!Guid.TryParse(taskId, out var guid))
            {
                await ReportErrorAsync(Error(taskId, FederatedTaskErrorCodes.InvalidTask, "the task id is not a valid GUID"));
                return Hold.None;
            }
            var storeKey = guid.ToString("D");

            // The review is read before the result: once a result has been reviewed, it is never computed again.
            var review = await _approval.GetAsync(storeKey, CancellationToken.None);

            // A file saved by an earlier poll is used as it is; the result is never computed twice.
            var bytes = await _outputs.TryGetAsync(storeKey, CancellationToken.None);

            if (bytes == null && review != null)
            {
                if (review.IsPending)
                {
                    Log.Error("Federated tasks: the saved result of task {TaskId} (reviewed sha256 {Reviewed}) is missing; not releasing it",
                        taskId, review.ResultSha256);
                    await ReportErrorAsync(ResultChanged(taskId));
                    return Hold.None;
                }

                await ActOnDecidedAsync(taskId, storeKey, review, null);
                return Hold.None;
            }

            if (bytes == null && task.State == TesState.PAUSEDEnum)
            {
                Log.Warning("Federated tasks: task {TaskId} is waiting for output approval, but the TRE has no saved result and no review for it; skipping it",
                    taskId);
                return Hold.None;
            }

            if (bytes == null)
            {
                var prepared = Prepare(task, taskId, treName);
                if (prepared.Skip) return Hold.None;
                if (prepared.ErrorReport != null)
                {
                    await ReportErrorAsync(prepared.ErrorReport);
                    return Hold.None;
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
                    return Hold.None;
                }

                if (checkOutcome.Kind == InputCheckKind.Held) return Hold.InputChecks;
                if (checkOutcome.Kind == InputCheckKind.Rejected)
                {
                    await ReportErrorAsync(Error(taskId, checkOutcome.ErrorCode!, checkOutcome.ErrorMessage));
                    return Hold.None;
                }

                // A QUEUED task is marked running before its data is read. A RUNNING one was marked by an earlier
                // poll and is resumed. A transport failure here throws and leaves the task for the next poll.
                if (task.State == TesState.QUEUEDEnum && !await ReportRunningAsync(taskId))
                    return Hold.None;

                // The task is running now. If the bucket fails during the read, it stays running and is resumed at the
                // next poll, under the same time limit as a failed listing. Any other fault cannot be fixed by
                // waiting, so it is reported at once.
                ExecutionResult executed;
                try
                {
                    executed = await _execution.RunAsync(task, prepared.Spec!, checkOutcome.FileKey!, CancellationToken.None);
                }
                catch (DatasetStoreException ex)
                {
                    await RetryOrGiveUpAsync(task, taskId, ex, "read");
                    return Hold.None;
                }
                catch (Exception ex)
                {
                    await ReportErrorAsync(InternalError(taskId, ex));
                    return Hold.None;
                }

                if (!executed.IsSuccess)
                {
                    await ReportErrorAsync(Error(taskId, executed.ErrorCode!, executed.ErrorMessage, executed.DatasetSha256));
                    return Hold.None;
                }

                bytes = FederatedResultDocumentJson.Serialize(executed.Document!);
                await _outputs.SaveAsync(storeKey, bytes, CancellationToken.None);
            }
            else
            {
                // A saved result passed the input checks on an earlier poll, so they don't run again: releasing it is
                // output approval's decision. If the site has since removed the task's image, say so in the log.
                var image = FederatedInputChecks.ImageOf(task);
                if (image != null && _checks.IsConfigured && !_checks.IsImageApproved(image))
                    Log.Warning("Federated tasks: the saved result of task {TaskId} goes on to output approval, although its image {Image} is no longer approved at this TRE; the input checks passed before the result was saved",
                        taskId, image);
            }

            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
            Log.Information("Federated tasks: saved result for {TaskId} sha256 {Sha}", taskId, sha);

            // A review that is still pending was made for one set of bytes. If the saved file differs, registering or
            // waiting would end in a SHA-256 mismatch at every decision, so the task is failed now.
            if (review is { IsPending: true } && !string.Equals(sha, review.ResultSha256, StringComparison.Ordinal))
            {
                Log.Error("Federated tasks: the saved result of task {TaskId} (sha256 {Saved}) does not match the pending review (sha256 {Reviewed}); not registering or waiting",
                    taskId, sha, review.ResultSha256);
                await ReportErrorAsync(ResultChanged(taskId));
                return Hold.None;
            }

            // The hospital approves each result before it leaves. A result not yet sent for review is registered with
            // the Egress app; a registration that failed part-way (pending record, no registration marker) is sent
            // again. A decided review came from the Egress app, so it needs no marker.
            if (review == null || (review.IsPending && !await _approval.IsRegisteredAsync(storeKey, CancellationToken.None)))
            {
                if (!_egress.IsConfigured) return Hold.Egress;
                review = await _approval.RegisterAsync(Registration(task, storeKey, bytes, sha), CancellationToken.None);
            }

            if (review.IsPending)
            {
                // Submission shows the task as waiting (PAUSED) until the reviewer decides.
                if (task.State != TesState.PAUSEDEnum) await ReportWaitingAsync(taskId);
                return Hold.None;
            }

            await ActOnDecidedAsync(taskId, storeKey, review, bytes);
            return Hold.None;
        }

        public async Task ActOnDecision(string taskId)
        {
            if (!_client.IsConfigured || !Guid.TryParse(taskId, out var guid))
            {
                Log.Warning("Federated tasks: cannot act on the decision for task {TaskId} now; the next poll does", taskId);
                return;
            }

            var storeKey = guid.ToString("D");
            var review = await _approval.GetAsync(storeKey, CancellationToken.None);
            if (review == null || review.IsPending)
            {
                Log.Warning("Federated tasks: task {TaskId} has no recorded decision; nothing to act on", taskId);
                return;
            }

            var bytes = await _outputs.TryGetAsync(storeKey, CancellationToken.None);
            await ActOnDecidedAsync(storeKey, storeKey, review, bytes);
        }

        /// <summary>
        /// Rejects or releases a decided task. Only the exact bytes the reviewer approved may leave; a rejection
        /// stands even when the file is gone. Shared by the poll and <see cref="ActOnDecision"/>.
        /// </summary>
        private async Task ActOnDecidedAsync(string taskId, string storeKey, OutputReview review, byte[]? bytes)
        {
            if (review.IsRejected)
            {
                await ReportErrorAsync(Rejection(taskId, review));
                return;
            }

            if (bytes == null)
            {
                Log.Error("Federated tasks: the saved result of task {TaskId} (reviewed sha256 {Reviewed}) is missing; not releasing it",
                    taskId, review.ResultSha256);
                await ReportErrorAsync(ResultChanged(taskId));
                return;
            }

            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
            if (!string.Equals(sha, review.ResultSha256, StringComparison.Ordinal))
            {
                Log.Error("Federated tasks: the saved result of task {TaskId} (sha256 {Saved}) does not match the reviewed result (sha256 {Reviewed}); not releasing it",
                    taskId, sha, review.ResultSha256);
                await ReportErrorAsync(ResultChanged(taskId));
                return;
            }

            var outcome = await _client.ReportAsync(
                new FederatedTaskResultReport { TaskId = taskId, State = TesState.COMPLETEEnum }, bytes);
            await HandleOutcomeAsync(taskId, storeKey, sha, bytes, outcome);
        }

        /// <summary>What the reviewer sees in the Egress app: the study, the code that ran and the result itself.</summary>
        private EgressRegistration Registration(TesTask task, string storeKey, byte[] bytes, string sha)
        {
            var executor = task.Executors?.FirstOrDefault();
            var command = string.Join(' ', executor?.Command ?? []);
            var parsed = FederatedTesProfile.Parse(task);
            // A saved result passed the profile when it was computed; fall back to the raw tag only in defence.
            var studyId = parsed.IsSuccess
                ? parsed.Spec!.StudyId
                : task.Tags?.GetValueOrDefault("study_id") ?? string.Empty;
            return new EgressRegistration(
                storeKey,
                $"Federated task {storeKey}: {command}",
                studyId,
                $"{executor?.Image} {command}".Trim(),
                System.Text.Encoding.UTF8.GetString(bytes),
                sha,
                _settings.OutputBucket);
        }

        /// <summary>Tells Submission the result is waiting for output approval; logs when it is not accepted.</summary>
        private async Task ReportWaitingAsync(string taskId)
        {
            var outcome = await _client.ReportProgressAsync(taskId, FederatedTaskStep.WaitingForOutputApproval);
            if (!outcome.Accepted)
                Log.Warning("Federated tasks: Submission did not accept the waiting-for-output-approval report for task {TaskId} (status {Status}, {ErrorCode}); it is sent again at the next poll",
                    taskId, outcome.StatusCode, outcome.ErrorCode);
        }

        /// <summary>The reviewer rejected the result: CANCELED at output approval, with the reviewer's reason.</summary>
        private static FederatedTaskResultReport Rejection(string taskId, OutputReview review)
        {
            Log.Information("Federated tasks: the result of task {TaskId} was rejected by output reviewer {Reviewer}", taskId, review.Agent);
            var reason = string.IsNullOrWhiteSpace(review.Description) ? "the output reviewer rejected the result" : review.Description;
            return new FederatedTaskResultReport
            {
                TaskId = taskId,
                State = TesState.CANCELEDEnum,
                RejectedAt = FederatedRejectedAt.OutputApproval,
                ErrorCode = FederatedTaskErrorCodes.RejectedByReviewer,
                ErrorMessage = FederatedResultLimits.TruncateErrorMessage(CleanControlChars(reason))
            };
        }

        /// <summary>The saved result is missing or differs from the one the reviewer saw; it is never released.</summary>
        private static FederatedTaskResultReport ResultChanged(string taskId)
        {
            // The caller logs what changed, with the SHA-256 values.
            return new FederatedTaskResultReport
            {
                TaskId = taskId,
                State = TesState.SYSTEMERROREnum,
                ErrorCode = FederatedTaskErrorCodes.ResultChangedAfterReview,
                ErrorMessage = "the saved result no longer matches the reviewed result"
            };
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
