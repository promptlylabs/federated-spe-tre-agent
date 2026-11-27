using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Submission.Api.Attributes;
using Submission.Api.ContractResolvers;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace Submission.Api.Controllers
{
    /// <summary>
    /// The GA4GH TES API (1.1) for federated studies, for TES clients such as DAAMS (ANZE-6134). A task here is a study
    /// version: CreateTask submits one (the same rules as FederatedTask/Enqueue), GetTask and ListTasks show it with a
    /// standard TES state, CancelTask cancels it. GetTask also shows one hospital's part by its task id. The 5S-TES native
    /// tasks are not served (docs/5s-tes-fork.md). No response contains result content.
    /// </summary>
    [ApiExplorerSettings(GroupName = "ga4gh-tes")]
    [ApiController]
    public class TaskServiceApiController : ControllerBase
    {
        private const string AdminRole = "dare-control-admin";

        /// <summary>Reading: an orchestrator admin (study versions) or a hospital's Agent (its own parts only).</summary>
        private const string ReadRoles = "dare-control-admin,dare-tre-admin";

        /// <summary>Upper bound on parts handed to a hospital in one ListTasks, as FederatedTask/GetPendingTasksForTre.</summary>
        private const int MaxPendingTasks = 50;
        private const int DefaultPageSize = 256;

        private static readonly Dictionary<TesView, JsonSerializerSettings> TesJsonSerializerSettings = new()
        {
            { TesView.MINIMAL, new JsonSerializerSettings { ContractResolver = MinimalTesTaskContractResolver.Instance } },
            { TesView.BASIC, new JsonSerializerSettings { ContractResolver = BasicTesTaskContractResolver.Instance } },
            { TesView.FULL, new JsonSerializerSettings { ContractResolver = FullTesTaskContractResolver.Instance } }
        };

        private readonly ApplicationDbContext _db;
        private readonly FederatedTaskSettings _settings;
        private readonly IFederatedTaskDispatch _dispatch;

        public TaskServiceApiController(ApplicationDbContext db, FederatedTaskSettings settings, IFederatedTaskDispatch dispatch)
        {
            _db = db;
            _settings = settings;
            _dispatch = dispatch;
        }

        /// <summary>Cancels a study version; a hospital's part is cancelled through its version.</summary>
        [HttpPost]
        [Authorize(Roles = AdminRole)]
        [Route("/v1/tasks/{id}:cancel")]
        [ValidateModelState]
        [SwaggerOperation("CancelTask")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesCancelTaskResponse), description: "")]
        [SwaggerResponse(statusCode: 400, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> CancelTask([FromRoute][Required] string id, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return TaskNotFound();
            }

            if (!await _db.FederatedStudyVersions.AnyAsync(v => v.VersionId == guid, cancellationToken))
            {
                var part = await _db.FederatedTasks.AsNoTracking().Include(t => t.StudyVersion)
                    .FirstOrDefaultAsync(t => t.TaskId == guid, cancellationToken);
                return part == null
                    ? TaskNotFound()
                    : Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                        $"task {id} is one hospital's part of study version {part.StudyVersion.VersionId}; cancel the version");
            }

            var result = await new FederatedStudyCancel(_db).CancelAsync(id, User, HttpContext, cancellationToken);
            return result.StatusCode switch
            {
                StatusCodes.Status200OK => Ok(new TesCancelTaskResponse()),
                StatusCodes.Status404NotFound => TaskNotFound(),   // removed since the check above
                _ => result
            };
        }

        /// <summary>Submits a study version. Only federated studies (with fed.* tags) are accepted.</summary>
        [HttpPost]
        [Authorize(Roles = AdminRole)]
        [Route("/v1/tasks")]
        [ValidateModelState]
        [SwaggerOperation("CreateTask")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesCreateTaskResponse), description: "")]
        [SwaggerResponse(statusCode: 400, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> CreateTaskAsync([FromBody] TesTask tesTask, CancellationToken cancellationToken)
        {
            if (tesTask.Tags?.Keys.Any(k => k.Equals(FederatedTesProfile.SitesTag, StringComparison.OrdinalIgnoreCase)) != true)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    $"this TES service accepts federated studies only (tags {FederatedTesProfile.SitesTag}, {FederatedTesProfile.StudyIdTag}, {FederatedTesProfile.CodeSha256Tag})");
            }

            return await new FederatedStudyIntake(_db, _settings, _dispatch).SubmitAsync(tesTask, User, HttpContext, cancellationToken);
        }

        /// <summary>The service and the federated profile it accepts: what a TES client reads first.</summary>
        [AllowAnonymous]
        [HttpGet]
        [Route("/v1/service-info")]
        [SwaggerOperation("GetServiceInfo")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesServiceInfo), description: "")]
        public virtual IActionResult GetServiceInfo() => StatusCode(StatusCodes.Status200OK, new TesServiceInfo
        {
            Id = _settings.ServiceId,
            Name = "SPMS federated TES",
            Type = new TesServiceType(),
            Description = "GA4GH TES 1.1, federated studies only. A task is a study version: required tags " +
                  $"{FederatedTesProfile.SitesTag} (2 or more hospitals, separated by '|'), {FederatedTesProfile.StudyIdTag} and " +
                  $"{FederatedTesProfile.CodeSha256Tag}; optional {FederatedTesProfile.RequestIdTag} (makes the submission " +
                  $"idempotent) and {FederatedTesProfile.RoundTag}. Pooled results only; no task returns a hospital's result. " +
                  "Hospital nodes fetch their parts with ListTasks and their own login. See docs/federated-tasks.md, \"The TES API\", " +
                  "and docs/node-contract.md.",
            Organization = new TesServiceOrganization { Name = _settings.OrganizationName, Url = _settings.OrganizationUrl },
            Version = _settings.ServiceVersion,
            Storage = [],
            TesResourcesSupportedBackendParameters = []
        });

        /// <summary>A study version by its id, or one hospital's part by its task id; a hospital's Agent reads only its own parts.</summary>
        [HttpGet]
        [Authorize(Roles = ReadRoles)]
        [Route("/v1/tasks/{id}")]
        [SwaggerOperation("GetTask")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesTask), description: "")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> GetTaskAsync([FromRoute][Required] string id, [FromQuery] string? view,
            CancellationToken cancellationToken)
        {
            if (ParseView(view) is not { } viewEnum)
            {
                return BadView();
            }

            if (!Guid.TryParse(id, out var guid))
            {
                return TaskNotFound();
            }

            // A hospital's Agent reads only its own parts; everything else is not found for it.
            var hospital = ControllerHelpers.GetAgentTre(User, _db);
            if (hospital != null)
            {
                var own = await _db.FederatedTasks.AsNoTracking().Include(t => t.PhaseChanges)
                    .FirstOrDefaultAsync(t => t.TaskId == guid && t.TreId == hospital.Id, cancellationToken);
                return own == null ? TaskNotFound() : TesJson(PartTask(own), viewEnum);
            }

            var version = await _db.FederatedStudyVersions.AsNoTracking().Include(v => v.Parts)
                .FirstOrDefaultAsync(v => v.VersionId == guid, cancellationToken);
            if (version != null)
            {
                return TesJson(FederatedStudyVersions.ToTesTask(version), viewEnum);
            }

            var part = await _db.FederatedTasks.AsNoTracking().Include(t => t.PhaseChanges)
                .FirstOrDefaultAsync(t => t.TaskId == guid, cancellationToken);
            if (part == null)
            {
                return TaskNotFound();
            }

            return TesJson(PartTask(part), viewEnum);
        }

        /// <summary>
        /// For an admin, study versions, newest first (hospitals' parts are in their version's metadata). For a hospital's
        /// Agent, its own pending parts: how a hospital fetches its work with a standard TES call.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = ReadRoles)]
        [Route("/v1/tasks")]
        [SwaggerOperation("ListTasks")]
        [SwaggerResponse(statusCode: 200, type: typeof(TesListTasksResponse), description: "")]
        public virtual async Task<IActionResult> ListTasks([FromQuery(Name = "name_prefix")] string? namePrefix,
            [FromQuery(Name = "page_size")] long? pageSize, [FromQuery(Name = "page_token")] string? pageToken,
            [FromQuery] string? view, CancellationToken cancellationToken, [FromQuery] string? state = null,
            [FromQuery(Name = "tag_key")] string[]? tagKey = null, [FromQuery(Name = "tag_value")] string[]? tagValue = null)
        {
            if (ParseView(view) is not { } viewEnum)
            {
                return BadView();
            }

            if (pageSize is < 1 or > 2047)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    "page_size must be more than 0 and less than 2048; it defaults to 256");
            }

            TesState? stateFilter = null;
            if (!string.IsNullOrEmpty(state))
            {
                stateFilter = TesStates.FromWire(state);
                if (stateFilter == null)
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "state is not a TES state");
                }
            }

            // TES 1.1: tag_key and tag_value are zipped; a key without a value (or with an empty one) matches any value.
            var tagFilters = (tagKey ?? []).Select((key, i) => (Key: key, Value: tagValue is { } v && i < v.Length ? v[i] : null)).ToList();
            bool Matches(TesTask t) =>
                (stateFilter == null || t.State == stateFilter)
                && tagFilters.All(f => t.Tags != null && t.Tags.TryGetValue(f.Key, out var value) && (string.IsNullOrEmpty(f.Value) || value == f.Value));

            // A hospital's Agent: its pending parts, most urgent first, as it polls for work (at most 50, one page).
            var hospital = ControllerHelpers.GetAgentTre(User, _db);
            if (hospital != null)
            {
                var pending = await FederatedHospitalTasks.PendingAsync(_db, hospital, MaxPendingTasks, cancellationToken);
                return TesJson(new TesListTasksResponse { Tasks = pending.Select(PartTask).Where(Matches).ToList() }, viewEnum);
            }

            var start = 0;
            if (!string.IsNullOrEmpty(pageToken) && !TryReadToken(pageToken, out start))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "page_token is not a token this service gave");
            }

            var size = (int)(pageSize ?? DefaultPageSize);
            // The name lives in the stored task, so the prefix is applied after reading. The PoC has few versions.
            var versions = await _db.FederatedStudyVersions.AsNoTracking().Include(v => v.Parts)
                .OrderByDescending(v => v.SubmittedAt).ThenByDescending(v => v.Id)
                .ToListAsync(cancellationToken);
            var tasks = versions.Select(FederatedStudyVersions.ToTesTask)
                .Where(t => string.IsNullOrEmpty(namePrefix) || (t.Name ?? "").StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(Matches)
                .ToList();
            var page = tasks.Skip(start).Take(size).ToList();
            var next = (long)start + size < tasks.Count
                ? Microsoft.AspNetCore.Authentication.Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes((start + size).ToString(CultureInfo.InvariantCulture)))
                : null;
            return TesJson(new TesListTasksResponse { Tasks = page, NextPageToken = next }, viewEnum);
        }

        /// <summary>
        /// A hospital's part as a TES task, without its result's hash or location: a small JSON result could be guessed from
        /// its hash, and TES clients such as DAAMS sit close to the researcher. A hospital's own Agent never needs them.
        /// </summary>
        private static TesTask PartTask(FederatedTask part)
        {
            var task = part.ToTesTask();
            foreach (var key in new[] { "result_bucket", "result_key", "result_sha256" })
            {
                task.Logs?.FirstOrDefault()?.Metadata?.Remove(key);
            }

            return task;
        }

        private static bool TryReadToken(string token, out int start)
        {
            start = 0;
            try
            {
                var text = Encoding.UTF8.GetString(Microsoft.AspNetCore.Authentication.Base64UrlTextEncoder.Decode(token));
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out start);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static TesView? ParseView(string? view) =>
            string.IsNullOrEmpty(view) ? TesView.MINIMAL
            : Enum.TryParse<TesView>(view, true, out var parsed) && Enum.IsDefined(parsed) ? parsed
            : null;

        private ObjectResult BadView() =>
            Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                $"view must be one of: {string.Join(", ", Enum.GetNames<TesView>())}");

        private static JsonResult TesJson(object value, TesView view) =>
            new(value, TesJsonSerializerSettings[view]) { StatusCode = StatusCodes.Status200OK };

        private static ObjectResult TaskNotFound() =>
            Error(StatusCodes.Status404NotFound, FederatedTaskErrorCodes.TaskNotFound, "task not found");

        private static ObjectResult Error(int status, string code, string message) =>
            new(new FederatedApiError { ErrorCode = code, ErrorMessage = message }) { StatusCode = status };

        private enum TesView
        {
            MINIMAL,
            BASIC,
            FULL
        }
    }
}
