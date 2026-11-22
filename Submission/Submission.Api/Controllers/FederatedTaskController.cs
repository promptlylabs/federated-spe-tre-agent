using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Serilog;
using Submission.Api.Models;
using System.Text.RegularExpressions;
using Submission.Api.Attributes;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace Submission.Api.Controllers
{
    /// <summary>
    /// Orchestrator API for GA4GH TES tasks in the federated profile: queue a task for a TRE, let the
    /// TRE Agent poll for and report on its tasks, and read a task back.
    /// </summary>
    [ApiExplorerSettings(GroupName = "v1")]
    [Route("api/[controller]")]
    [ApiController]
    public class FederatedTaskController : Controller
    {
        /// <summary>Attempts at one report when other reports for the same task keep winning the race.</summary>
        private const int MaxReportAttempts = 3;

        /// <summary>Upper bound on tasks returned to a TRE Agent in a single poll.</summary>
        private const int MaxPendingTasks = 50;

        private static readonly Regex Sha256Hex = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);

        private readonly ApplicationDbContext _DbContext;

        private readonly IFederatedResultStore _resultStore;

        private readonly FederatedTaskSettings _settings;

        public FederatedTaskController(ApplicationDbContext db, IFederatedResultStore resultStore, FederatedTaskSettings settings)
        {
            _DbContext = db;
            _resultStore = resultStore;
            _settings = settings;
        }

        [Authorize(Roles = "dare-control-admin")]
        [HttpPost]
        [Route("Enqueue")]
        [ValidateModelState]
        [SwaggerOperation("Enqueue")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesCreateTaskResponse), description: "")]
        [SwaggerResponse(statusCode: 400, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> Enqueue([FromBody] TesTask task, CancellationToken cancellationToken = default)
        {
            var parsed = FederatedTesProfile.Parse(task, _settings.ToIntakeLimits());
            if (!parsed.IsSuccess)
            {
                return Error(StatusCodes.Status400BadRequest, parsed.ErrorCode!, parsed.ErrorMessage!);
            }

            var spec = parsed.Spec!;
            var tre = await _DbContext.Tres
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Name.ToLower() == spec.TreName, cancellationToken);
            if (tre == null)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, $"TRE '{spec.TreName}' doesn't exist");
            }

            // Server-owned fields: callers must not be able to inject state, logs or results into a queued task.
            task.Id = null;
            task.State = TesState.UNKNOWNEnum;
            task.Logs = null;
            task.CreationTime = null;

            var entity = new FederatedTask
            {
                TaskId = Guid.NewGuid(),
                TreId = tre.Id,
                TreName = spec.TreName,
                StudyId = spec.StudyId,
                Operation = spec.Operation,
                Dataset = spec.Dataset,
                TesTaskJson = JsonConvert.SerializeObject(task),
                SubmittedAt = DateTime.UtcNow
            };
            entity.MoveTo(FederatedTaskPhase.Queued, entity.SubmittedAt);
            _DbContext.FederatedTasks.Add(entity);
            await _DbContext.SaveChangesAsync(cancellationToken);

            return Ok(new TesCreateTaskResponse { Id = entity.TaskId.ToString() });
        }

        [Authorize(Roles = "dare-tre-admin")]
        [HttpGet]
        [Route("GetPendingTasksForTre")]
        [ValidateModelState]
        [SwaggerOperation("GetPendingTasksForTre")]
        [SwaggerResponse(statusCode: 200, type: typeof(List<TesTask>), description: "")]
        [SwaggerResponse(statusCode: 403, description: "The caller is not a TRE Agent service account")]
        public virtual async Task<IActionResult> GetPendingTasksForTre(CancellationToken cancellationToken = default)
        {
            var tre = ControllerHelpers.GetAgentTre(User, _DbContext);
            if (tre == null)
            {
                return Forbid();
            }

            var treName = tre.Name.ToLower();

            // The Agent refuses tasks whose fed.sites is not its own name and leaves them as they are. Tasks enqueued under a
            // name the TRE no longer has would stay unfinished forever and fill the list, so they are not handed out;
            // except a canceling task, which the Agent cleans up before any name check (the rename sweep skips it too).
            // ToTesTask reads the history. Without the Include, lazy loading would run one query per task.
            var tasks = await _DbContext.FederatedTasks
                .AsNoTracking()
                .Include(x => x.PhaseChanges)
                .Where(x => x.TreId == tre.Id && (x.TreName == treName || x.Phase == FederatedTaskPhase.Canceling))
                .Where(FederatedTaskPhases.IsUnfinishedTask)
                // Cancelled first (the hospital cleans up), then running (the Agent may need to resume it), then
                // queued, then waiting: waiting tasks are only polled for their approval outcome, so they must not
                // crowd out tasks that need action.
                .OrderBy(x => x.Phase == FederatedTaskPhase.Canceling ? 0
                    : x.Phase == FederatedTaskPhase.Running ? 1
                    : x.Phase == FederatedTaskPhase.Queued ? 2 : 3)
                .ThenBy(x => x.SubmittedAt)
                .Take(MaxPendingTasks)
                .ToListAsync(cancellationToken);

            return Ok(tasks.Select(x => x.ToTesTask()).ToList());
        }

        [Authorize(Roles = "dare-tre-admin")]
        [HttpPost]
        [Route("ReportResult")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(FederatedResultLimits.MaxReportRequestBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = FederatedResultLimits.MaxReportRequestBytes)]
        [ValidateModelState]
        [SwaggerOperation("ReportResult")]
        [SwaggerResponse(statusCode: 200, type: typeof(FederatedReportResponse), description: "")]
        [SwaggerResponse(statusCode: 400, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 403, description: "The caller is not a TRE Agent service account")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 503, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> ReportResult([FromForm] FederatedTaskReportForm form, CancellationToken cancellationToken = default)
        {
            // Identity first: a caller that is not a TRE Agent learns nothing about the validation rules.
            var tre = ControllerHelpers.GetAgentTre(User, _DbContext);
            if (tre == null)
            {
                return Forbid();
            }

            var state = ParseWireState(form.State);
            if (state == null)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "State must be COMPLETE, EXECUTOR_ERROR, SYSTEM_ERROR or CANCELED");
            }

            if (state != TesState.CANCELEDEnum && form.RejectedAt is not null)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "RejectedAt is only accepted with state CANCELED");
            }

            var files = Request.Form.Files;
            FederatedTaskPhase? rejectedPhase = null;

            if (state != TesState.COMPLETEEnum)
            {
                // Without a code the orchestrator cannot tell what failed. Any well-formed code is accepted, not only
                // known ones, so a newer Agent's code is never refused (a refused report leaves the task QUEUED).
                if (form.ErrorCode is null || !FederatedResultLimits.ErrorCodePattern.IsMatch(form.ErrorCode))
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "ErrorCode is required and must be 1 to 64 lowercase letters or underscores");
                }

                if (!FederatedResultLimits.IsValidErrorMessage(form.ErrorMessage))
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, $"ErrorMessage must be at most {FederatedResultLimits.MaxErrorMessageLength} characters with no control characters");
                }

                if (state == TesState.CANCELEDEnum)
                {
                    rejectedPhase = form.RejectedAt switch
                    {
                        FederatedRejectedAt.InputChecks => FederatedTaskPhase.RejectedByInputChecks,
                        FederatedRejectedAt.OutputApproval => FederatedTaskPhase.RejectedAtOutputApproval,
                        _ => null
                    };
                    if (rejectedPhase == null)
                    {
                        return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                            $"RejectedAt must be {FederatedRejectedAt.InputChecks} or {FederatedRejectedAt.OutputApproval} for a CANCELED report");
                    }

                    if (string.IsNullOrWhiteSpace(form.ErrorMessage))
                    {
                        return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "ErrorMessage is required for a rejection");
                    }
                }

                if (files.Count > 0)
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "a file must not be sent with an error report");
                }

                if (form.DatasetSha256 is not null && !Sha256Hex.IsMatch(form.DatasetSha256))
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "DatasetSha256 must be 64 lowercase hex characters");
                }
            }
            else if (files.Count != 1 || files[0].Name != FederatedReportFields.ResultFile || form.DatasetSha256 is not null)
            {
                // DatasetSha256 comes from the result document on COMPLETE, so the form field is not accepted.
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    $"a COMPLETE report needs exactly one file part named '{FederatedReportFields.ResultFile}' and no DatasetSha256 field");
            }

            if (!Guid.TryParse(form.TaskId, out var taskId))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "TaskId must be a GUID");
            }

            // A task that belongs to another TRE is indistinguishable from one that doesn't exist.
            var task = await _DbContext.FederatedTasks
                .Include(x => x.PhaseChanges)
                .FirstOrDefaultAsync(x => x.TaskId == taskId && x.TreId == tre.Id, cancellationToken);
            if (task == null)
            {
                return TaskNotFound();
            }

            if (FederatedTaskPhases.IsFinal(task.Phase))
            {
                return AlreadyReported(task);
            }

            // Checked before any file is written: a cancelled task never releases a result, whatever was decided.
            if (task.Phase == FederatedTaskPhase.Canceling)
            {
                return TaskCanceling();
            }

            Action<FederatedTask> apply;
            var response = new FederatedReportResponse { Result = true };
            if (state == TesState.COMPLETEEnum)
            {
                var file = files[0];
                if (file.Length > FederatedResultLimits.MaxResultBytes)
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.ResultTooLarge,
                        $"the result file must be at most {FederatedResultLimits.MaxResultBytes} bytes");
                }

                byte[] bytes;
                await using (var stream = file.OpenReadStream())
                {
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken);
                    bytes = buffer.ToArray();
                }

                // Length is checked again on the bytes actually read, not only on the length the client declared.
                if (bytes.Length > FederatedResultLimits.MaxResultBytes)
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.ResultTooLarge,
                        $"the result file must be at most {FederatedResultLimits.MaxResultBytes} bytes");
                }

                var validation = FederatedResultDocumentJson.Validate(bytes, task);
                if (!validation.IsValid)
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidResult, validation.Message!);
                }

                // The client's file name is never used: the key is built only from server-known values.
                var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
                var key = FederatedResultKeys.For(task.TreId, task.TaskId, sha);
                try
                {
                    await _resultStore.PutAsync(key, bytes, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Storing the result of federated task {TaskId} for TRE {TreId} failed", task.TaskId, task.TreId);
                    return Error(StatusCodes.Status503ServiceUnavailable, FederatedTaskErrorCodes.StorageUnavailable,
                        "the result could not be stored, try again later");
                }

                var bucket = _resultStore.Bucket;
                var datasetSha = validation.Document!.DatasetSha256;
                apply = t =>
                {
                    t.MoveTo(FederatedTaskPhase.Released, DateTime.UtcNow);
                    t.ResultBucket = bucket;
                    t.ResultKey = key;
                    t.ResultSha256 = sha;
                    t.DatasetSha256 = datasetSha;
                };
                response.ResultBucket = bucket;
                response.ResultKey = key;
                response.ResultSha256 = sha;

                Log.Information("Stored result of federated task {TaskId} for TRE {TreId}: {Bytes} bytes, sha256 {Sha256}",
                    task.TaskId, task.TreId, bytes.Length, sha);
            }
            else
            {
                // ErrorCode was checked above; ErrorMessage was checked for a rejection.
                var errorCode = form.ErrorCode!;
                apply = rejectedPhase is { } rejected
                    ? t =>
                    {
                        t.Reject(rejected, DateTime.UtcNow, errorCode, form.ErrorMessage!);
                        t.DatasetSha256 = form.DatasetSha256;
                    }
                    : t =>
                    {
                        t.Fail(DateTime.UtcNow, state.Value, errorCode, form.ErrorMessage);
                        t.DatasetSha256 = form.DatasetSha256;
                    };
            }

            for (var attempt = 0; attempt < MaxReportAttempts; attempt++)
            {
                apply(task);
                try
                {
                    await _DbContext.SaveChangesAsync(cancellationToken);
                    return Ok(response);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another report moved the task after it was read here. A file stored above stays as an orphan if
                    // the other report wins: its key is unique to its content, and nothing references it.
                    _DbContext.ChangeTracker.Clear();
                    try
                    {
                        var current = await _DbContext.FederatedTasks
                            .Include(x => x.PhaseChanges)
                            .FirstOrDefaultAsync(x => x.TaskId == taskId && x.TreId == tre.Id, cancellationToken);
                        if (current == null)
                        {
                            return TaskNotFound();
                        }

                        if (FederatedTaskPhases.IsFinal(current.Phase))
                        {
                            return AlreadyReported(current);
                        }

                        if (current.Phase == FederatedTaskPhase.Canceling)
                        {
                            // A file stored above is deleted when the hospital confirms the cancel.
                            return TaskCanceling();
                        }

                        // A progress report moved it (e.g. to running): apply this final report on top of that.
                        task = current;
                    }
                    catch (Exception ex)
                    {
                        // Nothing was recorded, and it is not known whether the task is final: tell the Agent to retry.
                        Log.Warning(ex, "Re-reading federated task {TaskId} after a concurrent report failed", taskId);
                        return Error(StatusCodes.Status503ServiceUnavailable, FederatedTaskErrorCodes.StorageUnavailable,
                            "the report could not be recorded, try again later");
                    }
                }
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy,
                "the task is being updated by other reports, try again");
        }

        [Authorize(Roles = "dare-tre-admin")]
        [HttpPost]
        [Route("ReportProgress")]
        [ValidateModelState]
        [SwaggerOperation("ReportProgress")]
        [SwaggerResponse(statusCode: 200, type: typeof(FederatedProgressResponse), description: "")]
        [SwaggerResponse(statusCode: 400, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 403, description: "The caller is not a TRE Agent service account")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> ReportProgress([FromBody] FederatedTaskProgressReport report, CancellationToken cancellationToken = default)
        {
            var tre = ControllerHelpers.GetAgentTre(User, _DbContext);
            if (tre == null)
            {
                return Forbid();
            }

            if (!FederatedTaskPhases.TryParse(report.Phase, out var phase)
                || phase is not (FederatedTaskPhase.Running or FederatedTaskPhase.WaitingForOutputApproval or FederatedTaskPhase.Canceled))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    "Phase must be running, waiting_for_output_approval or canceled");
            }

            if (!Guid.TryParse(report.TaskId, out var taskId))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "TaskId must be a GUID");
            }

            for (var attempt = 0; attempt < MaxReportAttempts; attempt++)
            {
                // A task that belongs to another TRE is indistinguishable from one that doesn't exist.
                var task = await _DbContext.FederatedTasks
                    .Include(x => x.PhaseChanges)
                    .FirstOrDefaultAsync(x => x.TaskId == taskId && x.TreId == tre.Id, cancellationToken);
                if (task == null)
                {
                    return TaskNotFound();
                }

                // The phase graph lets a cancel take a queued task to canceled; the Agent may only confirm a cancel.
                if (phase == FederatedTaskPhase.Canceled && task.Phase == FederatedTaskPhase.Queued)
                {
                    return NotCancelled(task);
                }

                switch (FederatedTaskPhases.Check(task.Phase, phase))
                {
                    case FederatedTaskTransition.Canceling:
                        return TaskCanceling();
                    case FederatedTaskTransition.Invalid:
                        return NotCancelled(task);
                    case FederatedTaskTransition.NoChange:
                        return Ok(new FederatedProgressResponse { Phase = FederatedTaskPhases.ToWire(task.Phase) });
                    case FederatedTaskTransition.Backwards:
                        return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.InvalidTransition,
                            $"task is at phase '{FederatedTaskPhases.ToWire(task.Phase)}' and cannot go back to '{FederatedTaskPhases.ToWire(phase)}'");
                    case FederatedTaskTransition.AlreadyFinal:
                        return AlreadyReported(task);
                }

                if (phase == FederatedTaskPhase.Canceled)
                {
                    // A result stored while the cancel raced a report (or by any earlier attempt) goes before the task
                    // is final; a storage error refuses the confirmation, and the hospital confirms again next poll.
                    try
                    {
                        await _resultStore.DeleteTaskResultsAsync(task.TreId, task.TaskId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Deleting stored results of cancelled federated task {TaskId} failed", task.TaskId);
                        return Error(StatusCodes.Status503ServiceUnavailable, FederatedTaskErrorCodes.StorageUnavailable,
                            "the task's stored results could not be deleted, try again later");
                    }
                }

                task.MoveTo(phase, DateTime.UtcNow);
                try
                {
                    await _DbContext.SaveChangesAsync(cancellationToken);
                    return Ok(new FederatedProgressResponse { Phase = FederatedTaskPhases.ToWire(phase) });
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another report moved the task after it was read here. Drop the stale copy and decide again
                    // against the phase the winner left.
                    _DbContext.ChangeTracker.Clear();
                }
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy,
                "the task is being updated by other reports, try again");
        }

        /// <summary>
        /// Cancels a task. A queued task, which no hospital has picked up, is cancelled at once. A task a hospital has
        /// picked up waits in canceling until that hospital confirms it withdrew the review and removed the result.
        /// </summary>
        [Authorize(Roles = "dare-control-admin")]
        [HttpPost]
        [Route("Cancel")]
        [ValidateModelState]
        [SwaggerOperation("Cancel")]
        [SwaggerResponse(statusCode: 200, type: typeof(FederatedProgressResponse), description: "")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> Cancel([FromQuery] string id, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(id, out var taskId))
            {
                return TaskNotFound();
            }

            for (var attempt = 0; attempt < MaxReportAttempts; attempt++)
            {
                var task = await _DbContext.FederatedTasks
                    .Include(x => x.PhaseChanges)
                    .FirstOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
                if (task == null)
                {
                    return TaskNotFound();
                }

                switch (task.RequestCancel(DateTime.UtcNow))
                {
                    case FederatedTaskTransition.Apply:
                        break;
                    case FederatedTaskTransition.NoChange:
                        return Ok(new FederatedProgressResponse { Phase = FederatedTaskPhases.ToWire(task.Phase) });
                    case FederatedTaskTransition.AlreadyFinal:
                        return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskAlreadyFinished,
                            $"the task already finished at phase '{FederatedTaskPhases.ToWire(task.Phase)}'");
                    default:
                        return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.InvalidTransition,
                            $"a task at phase '{FederatedTaskPhases.ToWire(task.Phase)}' cannot be cancelled");
                }

                var next = task.Phase;
                try
                {
                    await _DbContext.SaveChangesAsync(cancellationToken);
                    Log.Information("Federated task {TaskId} cancelled by {User}: now {Phase}", taskId, User.FindFirst("preferred_username")?.Value,
                        FederatedTaskPhases.ToWire(next));
                    return Ok(new FederatedProgressResponse { Phase = FederatedTaskPhases.ToWire(next) });
                }
                catch (DbUpdateConcurrencyException)
                {
                    // An Agent report moved the task after it was read here: decide again against the phase it left.
                    _DbContext.ChangeTracker.Clear();
                }
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy,
                "the task is being updated by other reports, try again");
        }

        [Authorize(Roles = "dare-control-admin")]
        [HttpGet]
        [Route("GetTask")]
        [ValidateModelState]
        [SwaggerOperation("GetTask")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesTask), description: "")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> GetTask([FromQuery] string id, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(id, out var taskId))
            {
                return TaskNotFound();
            }

            var task = await _DbContext.FederatedTasks
                .AsNoTracking()
                .Include(x => x.PhaseChanges)
                .FirstOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
            if (task == null)
            {
                return TaskNotFound();
            }

            return Ok(task.ToTesTask());
        }

        // TODO: researcher access scoped by study once study_id → project mapping exists
        [Authorize(Roles = "dare-control-admin")]
        [HttpGet]
        [Route("GetTaskResult")]
        [ValidateModelState]
        [SwaggerOperation("GetTaskResult")]
        [SwaggerResponse(statusCode: 200, description: "The result file (application/json attachment)")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 500, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 503, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> GetTaskResult([FromQuery] string id, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(id, out var taskId))
            {
                return TaskNotFound();
            }

            var task = await _DbContext.FederatedTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
            if (task == null)
            {
                return TaskNotFound();
            }

            if (task.State != TesState.COMPLETEEnum || task.ResultKey == null || task.ResultBucket == null)
            {
                return Error(StatusCodes.Status404NotFound, FederatedTaskErrorCodes.ResultNotFound, "task has no result");
            }

            byte[]? bytes;
            try
            {
                bytes = await _resultStore.GetAsync(task.ResultBucket, task.ResultKey, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reading the result of federated task {TaskId} failed", task.TaskId);
                return Error(StatusCodes.Status503ServiceUnavailable, FederatedTaskErrorCodes.StorageUnavailable,
                    "the result could not be read, try again later");
            }

            if (bytes == null)
            {
                Log.Error("The result object of COMPLETE federated task {TaskId} is missing from storage", task.TaskId);
                return Error(StatusCodes.Status404NotFound, FederatedTaskErrorCodes.ResultNotFound, "the result file is missing from storage");
            }

            // Never serve bytes that do not match what was validated and recorded at upload.
            if (FederatedResultDocumentJson.Sha256Hex(bytes) != task.ResultSha256)
            {
                Log.Error("The stored result of federated task {TaskId} failed its integrity check", task.TaskId);
                return Error(StatusCodes.Status500InternalServerError, FederatedTaskErrorCodes.InternalError,
                    "stored result failed its integrity check");
            }

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(bytes, "application/json", $"{task.TaskId}.json");
        }

        /// <summary>
        /// Runs before [ApiController]'s automatic 400, so a body that fails binding (bad JSON, unknown enum value)
        /// gets the same error body as every other 4xx here instead of ValidationProblemDetails.
        /// </summary>
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (!context.ModelState.IsValid)
            {
                var problems = context.ModelState
                    .Where(x => x.Value!.Errors.Count > 0)
                    .Select(x => string.IsNullOrEmpty(x.Key)
                        ? x.Value!.Errors[0].ErrorMessage
                        : $"{x.Key}: {x.Value!.Errors[0].ErrorMessage}");
                context.Result = Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    "request is not valid: " + string.Join("; ", problems));
                return;
            }

            base.OnActionExecuting(context);
        }

        private static ObjectResult Error(int status, string code, string message) =>
            new(new FederatedApiError { ErrorCode = code, ErrorMessage = message }) { StatusCode = status };

        private static ObjectResult TaskNotFound() =>
            Error(StatusCodes.Status404NotFound, FederatedTaskErrorCodes.TaskNotFound, "task not found");

        private static ObjectResult NotCancelled(FederatedTask task) =>
            Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.InvalidTransition,
                $"task is at phase '{FederatedTaskPhases.ToWire(task.Phase)}' and was not cancelled");

        private static ObjectResult TaskCanceling() =>
            Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskCanceling,
                "the task is being cancelled; only the cancel confirmation is accepted");

        private static TesState? ParseWireState(string? state) => state switch
        {
            "COMPLETE" => TesState.COMPLETEEnum,
            "EXECUTOR_ERROR" => TesState.EXECUTORERROREnum,
            "SYSTEM_ERROR" => TesState.SYSTEMERROREnum,
            "CANCELED" => TesState.CANCELEDEnum,
            _ => null
        };

        /// <summary>
        /// 409. For a task already COMPLETE the body carries the stored result's sha256 and key, because the Agent cannot
        /// call GetTask.
        /// </summary>
        private static ObjectResult AlreadyReported(FederatedTask existing)
        {
            var result = Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskAlreadyReported, "a result has already been reported for this task");
            if (existing.State == TesState.COMPLETEEnum)
            {
                var body = (FederatedApiError)result.Value!;
                body.ResultSha256 = existing.ResultSha256;
                body.ResultKey = existing.ResultKey;
            }

            return result;
        }
    }
}
