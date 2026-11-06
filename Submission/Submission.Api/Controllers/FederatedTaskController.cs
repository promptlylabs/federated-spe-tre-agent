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
        /// <summary>Upper bound on tasks returned to a TRE Agent in a single poll.</summary>
        private const int MaxPendingTasks = 50;

        private static readonly Regex Sha256Hex = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);

        private readonly ApplicationDbContext _DbContext;

        private readonly IFederatedResultStore _resultStore;

        public FederatedTaskController(ApplicationDbContext db, IFederatedResultStore resultStore)
        {
            _DbContext = db;
            _resultStore = resultStore;
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
            var parsed = FederatedTesProfile.Parse(task);
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
                State = TesState.QUEUEDEnum,
                SubmittedAt = DateTime.UtcNow
            };
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

            // The Agent refuses tasks whose tres is not its own name and leaves them QUEUED. Tasks enqueued under a
            // name the TRE no longer has would sit at the front of the queue forever, so they are not handed out.
            var tasks = await _DbContext.FederatedTasks
                .AsNoTracking()
                .Where(x => x.TreId == tre.Id && x.State == TesState.QUEUEDEnum && x.TreName == treName)
                .OrderBy(x => x.SubmittedAt)
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
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "State must be COMPLETE, EXECUTOR_ERROR or SYSTEM_ERROR");
            }

            var files = Request.Form.Files;
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
                .FirstOrDefaultAsync(x => x.TaskId == taskId && x.TreId == tre.Id, cancellationToken);
            if (task == null)
            {
                return TaskNotFound();
            }

            if (task.State != TesState.QUEUEDEnum)
            {
                return AlreadyReported(task);
            }

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

                task.State = TesState.COMPLETEEnum;
                task.CompletedAt = DateTime.UtcNow;
                task.ResultBucket = _resultStore.Bucket;
                task.ResultKey = key;
                task.ResultSha256 = sha;
                task.DatasetSha256 = validation.Document!.DatasetSha256;
                response.ResultBucket = task.ResultBucket;
                response.ResultKey = key;
                response.ResultSha256 = sha;

                Log.Information("Stored result of federated task {TaskId} for TRE {TreId}: {Bytes} bytes, sha256 {Sha256}",
                    task.TaskId, task.TreId, bytes.Length, sha);
            }
            else
            {
                task.State = state.Value;
                task.CompletedAt = DateTime.UtcNow;
                task.DatasetSha256 = form.DatasetSha256;
                task.ErrorCode = form.ErrorCode;
                task.ResultErrorMessage = form.ErrorMessage;
            }

            try
            {
                await _DbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another report for this task was saved after this one read it as QUEUED. A file stored above stays
                // as an orphan: its key is unique to its content, and nothing references it.
                FederatedTask? winner = null;
                try
                {
                    winner = await _DbContext.FederatedTasks.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.TaskId == task.TaskId, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Re-reading federated task {TaskId} after a concurrent report failed", task.TaskId);
                }

                return AlreadyReported(winner);
            }

            return Ok(response);
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

        private static TesState? ParseWireState(string? state) => state switch
        {
            "COMPLETE" => TesState.COMPLETEEnum,
            "EXECUTOR_ERROR" => TesState.EXECUTORERROREnum,
            "SYSTEM_ERROR" => TesState.SYSTEMERROREnum,
            _ => null
        };

        /// <summary>
        /// 409. For a task already COMPLETE the body carries the stored result's sha256 and key, because the Agent cannot
        /// call GetTask.
        /// </summary>
        private static ObjectResult AlreadyReported(FederatedTask? existing)
        {
            var result = Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskAlreadyReported, "a result has already been reported for this task");
            if (existing is { State: TesState.COMPLETEEnum })
            {
                var body = (FederatedApiError)result.Value!;
                body.ResultSha256 = existing.ResultSha256;
                body.ResultKey = existing.ResultKey;
            }

            return result;
        }
    }
}
