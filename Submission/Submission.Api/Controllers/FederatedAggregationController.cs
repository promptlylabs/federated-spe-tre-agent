using System.Text;
using System.Text.Json;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace Submission.Api.Controllers
{
    /// <summary>
    /// Pooling a study version's released results (ANZE-6132). The aggregator, an Agent in the aggregator role, takes
    /// ready versions and runs the researcher's aggregate() in a locked-down container; Submission never starts one.
    /// Nothing aggregate() produced is ever logged here: only version IDs, error codes and hashes.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class FederatedAggregationController : ControllerBase
    {
        private const string AggregatorRole = "dare-federated-aggregator";
        private const int MaxSaveAttempts = 3;

        /// <summary>Fixed text per failure code; the aggregator's own text is never taken.</summary>
        private static readonly IReadOnlyDictionary<string, string> FailureMessages = new Dictionary<string, string>
        {
            [FederatedTaskErrorCodes.InvalidTask] = "the stored code does not match its hash, so it was not run",
            [FederatedTaskErrorCodes.ImageNotApproved] = "the task's image is not approved for pooling at the orchestrator",
            [FederatedTaskErrorCodes.UnsafeCode] = "the orchestrator's code scan refused the code, so aggregate() was not run",
            [FederatedTaskErrorCodes.CodeError] = "aggregate() could not be imported, is missing, or raised an exception",
            [FederatedTaskErrorCodes.InvalidResult] = "aggregate() returned a value the orchestrator cannot accept",
            [FederatedTaskErrorCodes.ResultTooLarge] = "aggregate() returned a result larger than the limit",
            [FederatedTaskErrorCodes.Timeout] = "aggregate() ran longer than the orchestrator's limit",
            [FederatedTaskErrorCodes.OutOfMemory] = "aggregate() ran out of memory",
            [FederatedTaskErrorCodes.RunFailed] = "the pooling run ended without a result; an admin can retry",
            [FederatedTaskErrorCodes.InternalError] = "the orchestrator could not pool the results; an admin can retry"
        };

        private readonly ApplicationDbContext _db;
        private readonly IFederatedResultStore _store;

        public FederatedAggregationController(ApplicationDbContext db, IFederatedResultStore store)
        {
            _db = db;
            _store = store;
        }

        /// <summary>
        /// Versions ready for aggregation, oldest first, each with the released hospitals' values only. A version whose
        /// stored hospital result can't be read back fails with internal_error; one whose storage is unreachable waits.
        /// </summary>
        [Authorize(Roles = AggregatorRole)]
        [HttpGet]
        [Route("GetPendingAggregations")]
        [SwaggerResponse(statusCode: 200, type: typeof(List<FederatedAggregationJob>), description: "")]
        public virtual async Task<IActionResult> GetPendingAggregations(CancellationToken cancellationToken = default)
        {
            var ready = await _db.FederatedStudyVersions
                .Include(x => x.Parts)
                .Where(x => x.Outcome == FederatedStudyOutcome.ReadyForAggregation)
                .OrderBy(x => x.ReadyAt).ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var jobs = new List<FederatedAggregationJob>();
            foreach (var version in ready)
            {
                (string Results, string Evidence)? read;
                try
                {
                    read = await ReadResultsAsync(version, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "Federated aggregation: the results of study version {VersionId} could not be read; it is tried again at the next poll",
                        version.VersionId);
                    continue;
                }

                if (read is not { } results)
                {
                    FederatedStudyOutcomes.Fail(version, DateTime.UtcNow, FederatedTaskErrorCodes.InternalError,
                        "a released hospital result could not be read back; an admin can retry");
                    try
                    {
                        await _db.SaveChangesAsync(cancellationToken);
                        Log.Error("Federated aggregation: a stored hospital result of study version {VersionId} is missing or fails its hash; failed with internal_error",
                            version.VersionId);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _db.Entry(version).State = EntityState.Detached;
                    }

                    continue;
                }

                jobs.Add(new FederatedAggregationJob
                {
                    VersionId = version.VersionId,
                    StudyId = version.StudyId,
                    Version = version.Version,
                    TesTaskJson = version.TesTaskJson,
                    CodeSha256 = version.CodeSha256,
                    ResultsJson = results.Results,
                    EvidenceJson = results.Evidence
                });
            }

            return Ok(jobs);
        }

        /// <summary>
        /// The released hospitals' result.value, in fed.sites order, as a JSON array, and their result.acro (null where absent)
        /// in the same order; null when a stored file is missing,
        /// fails its hash or isn't a result document. Throws when storage is unreachable.
        /// </summary>
        private async Task<(string Results, string Evidence)?> ReadResultsAsync(FederatedStudyVersion version, CancellationToken ct)
        {
            var sites = version.Sites.Split('|').ToList();
            var values = new List<string>();
            var evidence = new List<string>();
            foreach (var part in version.Parts.Where(p => p.Phase == FederatedTaskPhase.Released).OrderBy(p => sites.IndexOf(p.TreName)))
            {
                if (part.ResultBucket is null || part.ResultKey is null || part.ResultSha256 is null)
                {
                    return null;
                }

                var bytes = await _store.GetAsync(part.ResultBucket, part.ResultKey, ct);
                if (bytes == null || FederatedResultDocumentJson.Sha256Hex(bytes) != part.ResultSha256)
                {
                    return null;
                }

                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    var result = doc.RootElement.GetProperty("result");
                    values.Add(result.GetProperty("value").GetRawText());
                    evidence.Add(result.TryGetProperty("acro", out var acro) ? acro.GetRawText() : "null");
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    return null;
                }
            }

            return ("[" + string.Join(",", values) + "]", "[" + string.Join(",", evidence) + "]");
        }

        /// <summary>
        /// The aggregator's report: running (records the start), aggregated (with the pooled document as file part
        /// <c>result</c>; stored, then the outcome) or failed (with one of the failure codes; fixed message). Only a version
        /// still ready for aggregation takes one; repeating the report that already stuck is 200.
        /// </summary>
        [Authorize(Roles = AggregatorRole)]
        [HttpPost]
        [Route("ReportAggregation")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(FederatedResultLimits.MaxReportRequestBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = FederatedResultLimits.MaxReportRequestBytes)]
        [SwaggerResponse(statusCode: 200, type: typeof(FederatedStudyVersionView), description: "")]
        [SwaggerResponse(statusCode: 400, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 503, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> ReportAggregation([FromForm] FederatedAggregationReportForm form,
            [FromForm(Name = FederatedReportFields.ResultFile)] IFormFile? result, CancellationToken cancellationToken = default)
        {
            var phase = form.Phase;
            if (phase is not (FederatedAggregationPhases.Running or FederatedAggregationPhases.Aggregated or FederatedAggregationPhases.Failed))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "Phase must be running, aggregated or failed");
            }

            if (phase == FederatedAggregationPhases.Failed
                && (form.ErrorCode is null || !FederatedAggregationPhases.FailureCodes.Contains(form.ErrorCode)))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    "ErrorCode must be one of " + string.Join(", ", FederatedAggregationPhases.FailureCodes.Order()));
            }

            if (form.ErrorLine is { } line && (form.ErrorCode != FederatedTaskErrorCodes.CodeError || line < 1))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask, "ErrorLine is only taken with code_error, and is 1 or more");
            }

            if ((phase == FederatedAggregationPhases.Aggregated) != (result != null))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    $"an aggregated report needs exactly the file part '{FederatedReportFields.ResultFile}', and no other report takes one");
            }

            if (!Guid.TryParse(form.VersionId, out var versionId))
            {
                return StudyNotFound();
            }

            byte[]? bytes = null;
            if (result != null)
            {
                if (result.Length > FederatedResultLimits.MaxResultBytes)
                {
                    return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.ResultTooLarge,
                        $"the pooled result must be at most {FederatedResultLimits.MaxResultBytes} bytes");
                }

                await using var stream = result.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                bytes = buffer.ToArray();
            }

            for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
            {
                var version = await _db.FederatedStudyVersions.Include(x => x.Parts)
                    .FirstOrDefaultAsync(x => x.VersionId == versionId, cancellationToken);
                if (version == null)
                {
                    return StudyNotFound();
                }

                if (version.Outcome != FederatedStudyOutcome.ReadyForAggregation)
                {
                    return IsRepeat(version, phase, form.ErrorCode, bytes)
                        ? Ok(FederatedStudyVersions.ToView(version))
                        : Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskAlreadyFinished,
                            $"version {version.Version} of study '{version.StudyId}' is {version.Outcome?.ToWire() ?? "open"}, not ready for aggregation");
                }

                var now = DateTime.UtcNow;
                // Also for a final report with no running before it (lost, or an earlier run collected after a retry), so
                // the audit trail always has a start.
                version.AggregationStartedAt ??= now;
                switch (phase)
                {
                    case FederatedAggregationPhases.Running:
                        break;
                    case FederatedAggregationPhases.Failed:
                        // The line helps the researcher find the fault; it's the only detail taken, and it's a number.
                        FederatedStudyOutcomes.Fail(version, now, form.ErrorCode!,
                            FailureMessages[form.ErrorCode!] + (form.ErrorLine is { } failedLine ? $" (analysis.py line {failedLine})" : ""));
                        break;
                    default:
                        var released = version.Parts.Count(p => p.Phase == FederatedTaskPhase.Released);
                        if (FederatedPooledResultDocument.Validate(bytes!, version, released) is { } problem)
                        {
                            return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidResult, problem);
                        }

                        var sha = FederatedResultDocumentJson.Sha256Hex(bytes!);
                        var key = FederatedResultKeys.ForPooled(version.VersionId, sha);
                        try
                        {
                            await _store.PutAsync(key, bytes!, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Error(ex, "Federated aggregation: storing the pooled result of study version {VersionId} failed", versionId);
                            return Error(StatusCodes.Status503ServiceUnavailable, FederatedTaskErrorCodes.StorageUnavailable,
                                "the pooled result could not be stored, try again later");
                        }

                        FederatedStudyOutcomes.Aggregate(version, now, _store.Bucket, key, sha);
                        FederatedAudit.Add(_db, FiveSafesTes.Core.Models.LogType.FederatedPooledResultStored, User, HttpContext,
                            new { versionId = version.VersionId, studyId = version.StudyId, version = version.Version, pooledResultSha256 = sha });
                        break;
                }

                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The sweep or an admin changed the version meanwhile: decide again against what they left.
                    _db.ChangeTracker.Clear();
                    continue;
                }

                Log.Information("Federated aggregation: study version {VersionId} reported {Phase}{Code}{Sha}", versionId, phase,
                    form.ErrorCode is null ? "" : " " + form.ErrorCode, version.PooledResultSha256 is null ? "" : ", pooled sha256 " + version.PooledResultSha256);
                return Ok(FederatedStudyVersions.ToView(version));
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy, "the study version is being updated, try again");
        }

        /// <summary>The report that already decided the version, sent again (its response was lost).</summary>
        private static bool IsRepeat(FederatedStudyVersion version, string phase, string? errorCode, byte[]? bytes) => phase switch
        {
            FederatedAggregationPhases.Aggregated => version.Outcome == FederatedStudyOutcome.Aggregated
                && bytes != null && FederatedResultDocumentJson.Sha256Hex(bytes) == version.PooledResultSha256,
            FederatedAggregationPhases.Failed => version.Outcome == FederatedStudyOutcome.Failed && version.AggregationErrorCode == errorCode,
            _ => false
        };

        /// <summary>
        /// Puts a version whose aggregation failed for an infrastructure reason (aggregation_timeout, run_failed,
        /// internal_error) back to ready for aggregation. A failure of the researcher's code can't be retried: the same
        /// code on the same results and limits would fail again.
        /// </summary>
        [Authorize(Roles = "dare-control-admin")]
        [HttpPost]
        [Route("RetryAggregation")]
        [SwaggerResponse(statusCode: 200, type: typeof(FederatedStudyVersionView), description: "")]
        [SwaggerResponse(statusCode: 404, type: typeof(FederatedApiError), description: "")]
        [SwaggerResponse(statusCode: 409, type: typeof(FederatedApiError), description: "")]
        public virtual async Task<IActionResult> RetryAggregation([FromQuery] string id, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(id, out var versionId))
            {
                return StudyNotFound();
            }

            for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
            {
                var version = await _db.FederatedStudyVersions.Include(x => x.Parts)
                    .FirstOrDefaultAsync(x => x.VersionId == versionId, cancellationToken);
                if (version == null)
                {
                    return StudyNotFound();
                }

                if (version.Outcome != FederatedStudyOutcome.Failed || version.AggregationErrorCode is not { } code
                    || !FederatedStudyOutcomeReasons.RetryableAggregationCodes.Contains(code))
                {
                    return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskAlreadyFinished,
                        "only a version whose aggregation failed with aggregation_timeout, run_failed or internal_error can be retried");
                }

                var now = DateTime.UtcNow;
                version.Outcome = FederatedStudyOutcome.ReadyForAggregation;
                version.ReadyAt = now;
                version.AggregationStartedAt = null;
                version.AggregationErrorCode = null;
                version.OutcomeMessage = $"retried by an orchestrator admin after {code}";
                version.OutcomeAt = now;
                FederatedAudit.Add(_db, FiveSafesTes.Core.Models.LogType.FederatedAggregationRetried, User, HttpContext,
                    new { versionId = version.VersionId, studyId = version.StudyId, version = version.Version, after = code });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _db.ChangeTracker.Clear();
                    continue;
                }

                Log.Information("Federated aggregation: study version {VersionId} retried by {User} after {Code}", versionId,
                    User.FindFirst("preferred_username")?.Value, code);
                return Ok(FederatedStudyVersions.ToView(version));
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy, "the study version is being updated, try again");
        }

        private static ObjectResult Error(int status, string code, string message) =>
            new(new FederatedApiError { ErrorCode = code, ErrorMessage = message }) { StatusCode = status };

        private static ObjectResult StudyNotFound() =>
            Error(StatusCodes.Status404NotFound, FederatedTaskErrorCodes.StudyNotFound, "study or version not found");
    }
}
