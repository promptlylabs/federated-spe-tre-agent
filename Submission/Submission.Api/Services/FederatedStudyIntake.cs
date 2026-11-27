using System.Security.Claims;
using System.Text.RegularExpressions;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Serilog;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// Accepting a study version: the profile, at least 2 hospitals, an aggregate(), registered TREs, one open version per
    /// study, idempotency by fed.request_id, the fan-out to one part per hospital, and the audit entry. Shared by
    /// FederatedTask/Enqueue and the TES CreateTask route, so both apply the same rules.
    /// </summary>
    public sealed class FederatedStudyIntake
    {
        /// <summary>Attempts at one submission when concurrent submissions of the same study keep taking the version number.</summary>
        private const int MaxEnqueueAttempts = 3;

        // A top-level def aggregate(…), allowing extra spaces; a byte-order mark before the first line is ignored below.
        private static readonly Regex AggregateDefinition = new(@"^def\s+aggregate\s*\(", RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly ApplicationDbContext _db;
        private readonly FederatedTaskSettings _settings;
        private readonly IFederatedTaskDispatch _dispatch;

        public FederatedStudyIntake(ApplicationDbContext db, FederatedTaskSettings settings, IFederatedTaskDispatch dispatch)
        {
            _db = db;
            _settings = settings;
            _dispatch = dispatch;
        }

        /// <summary>200 with the version's id (<see cref="TesCreateTaskResponse"/>), or the refusal as a <see cref="FederatedApiError"/>.</summary>
        public async Task<ObjectResult> SubmitAsync(TesTask task, ClaimsPrincipal user, HttpContext http, CancellationToken cancellationToken)
        {
            var parsed = FederatedTesProfile.Parse(task, _settings.ToIntakeLimits());
            if (!parsed.IsSuccess)
            {
                return Error(StatusCodes.Status400BadRequest, parsed.ErrorCode!, parsed.ErrorMessage!);
            }

            var spec = parsed.Spec!;
            // Here, not in FederatedTesProfile.Parse: the Agent parses each hospital's part, which names one site.
            if (spec.Sites.Count < FederatedStudyOutcomeReasons.MinimumHospitals)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    $"a study needs at least {FederatedStudyOutcomeReasons.MinimumHospitals} hospitals in fed.sites, so that no single hospital's result is released");
            }

            // The orchestrator pools the hospitals' results with the same file's aggregate(); without it every hospital
            // would run and review the study for nothing. A function defined some other way still fails at the aggregator.
            var code = task.Inputs?.FirstOrDefault(i => i.Path == FederatedTesProfile.CodePath)?.Content ?? "";
            if (!AggregateDefinition.IsMatch(code.TrimStart('\uFEFF')))
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    "the code has no aggregate(results) function; the orchestrator needs it to pool the hospitals' results. " +
                    "Define it at the top level of the file, as 'def aggregate(results):' (not inside a class or another function)");
            }

            var tres = await _db.Tres
                .AsNoTracking()
                .Where(x => spec.Sites.Contains(x.Name.ToLower()))
                .ToListAsync(cancellationToken);
            var tresBySite = tres.DistinctBy(x => x.Name.ToLower()).ToDictionary(x => x.Name.ToLower());
            var missing = spec.Sites.Where(x => !tresBySite.ContainsKey(x)).ToList();
            if (missing.Count > 0)
            {
                return Error(StatusCodes.Status400BadRequest, FederatedTaskErrorCodes.InvalidTask,
                    $"TRE(s) {string.Join(", ", missing.Select(x => $"'{x}'"))} don't exist");
            }

            FederatedStudyFanOut.ClearServerOwnedFields(task);
            var tesTaskJson = JsonConvert.SerializeObject(task);

            for (var attempt = 0; attempt < MaxEnqueueAttempts; attempt++)
            {
                // A repeated fed.request_id: the version it created, or a conflict if the content differs. Before the
                // one-open-version rule, so a retry of an accepted submission is not refused as study_version_open.
                if (spec.RequestId is { } requestId)
                {
                    var earlier = await _db.FederatedStudyVersions.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
                    if (earlier != null)
                    {
                        return earlier.StudyId == spec.StudyId && earlier.CodeSha256 == spec.CodeSha256 && earlier.Sites == string.Join('|', spec.Sites)
                            ? new OkObjectResult(new TesCreateTaskResponse { Id = earlier.VersionId.ToString() })
                            : Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.RequestIdConflict,
                                "fed.request_id was already used for a different submission");
                    }
                }

                // Only the latest version can block: an older one is either ended or was followed by a newer submission.
                var latest = await _db.FederatedStudyVersions
                    .AsNoTracking()
                    .Include(x => x.Parts)
                    .Where(x => x.StudyId == spec.StudyId)
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefaultAsync(cancellationToken);
                if (latest?.State == FederatedStudyVersionState.Open)
                {
                    var conflict = Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.StudyVersionOpen,
                        $"version {latest.Version} of study '{spec.StudyId}' is still open; cancel it or wait for it to end");
                    ((FederatedApiError)conflict.Value!).VersionId = latest.VersionId;
                    return conflict;
                }

                var submittedAt = DateTime.UtcNow;
                var version = new FederatedStudyVersion
                {
                    VersionId = Guid.NewGuid(),
                    StudyId = spec.StudyId,
                    Version = (latest?.Version ?? 0) + 1,
                    Sites = string.Join('|', spec.Sites),
                    CodeSha256 = spec.CodeSha256,
                    TesTaskJson = tesTaskJson,
                    SubmittedAt = submittedAt,
                    SubmittedBy = user.FindFirst("preferred_username")?.Value,
                    RequestId = spec.RequestId,
                    DeadlineAt = submittedAt.AddMinutes(_settings.StudyTimeoutMinutes)
                };
                foreach (var site in spec.Sites)
                {
                    var part = FederatedStudyFanOut.BuildPart(task, version, tresBySite[site], site);
                    part.Operation = spec.Operation;
                    part.Dataset = spec.Dataset;
                    part.Round = spec.Round;
                    version.Parts.Add(part);
                }

                _db.FederatedStudyVersions.Add(version);
                FederatedAudit.Add(_db, FiveSafesTes.Core.Models.LogType.FederatedStudySubmitted, user, http,
                    new { versionId = version.VersionId, studyId = version.StudyId, version = version.Version, codeSha256 = version.CodeSha256, sites = version.Sites });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
                {
                    // A concurrent submission took this version number (unique StudyId, Version): read the study again.
                    // Any other save error (a TRE deleted meanwhile, the database down) is not a collision and fails.
                    Log.Warning(ex, "Enqueueing version {Version} of study {StudyId} collided with another submission", version.Version, spec.StudyId);
                    _db.ChangeTracker.Clear();
                    continue;
                }

                foreach (var part in version.Parts)
                {
                    await _dispatch.SendAsync(part, cancellationToken);
                }

                return new OkObjectResult(new TesCreateTaskResponse { Id = version.VersionId.ToString() });
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy,
                "the study is being submitted by other requests, try again");
        }

        private static ObjectResult Error(int status, string code, string message) =>
            new(new FederatedApiError { ErrorCode = code, ErrorMessage = message }) { StatusCode = status };
    }
}
