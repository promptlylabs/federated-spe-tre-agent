using System.Security.Claims;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// Cancelling a study version: a queued part at once, a picked-up part through canceling until its hospital confirms,
    /// with the audit entry. Shared by FederatedTask/Cancel and the TES CancelTask route.
    /// </summary>
    public sealed class FederatedStudyCancel
    {
        /// <summary>Attempts when hospital reports keep changing the version while it is cancelled.</summary>
        private const int MaxAttempts = 3;

        private readonly ApplicationDbContext _db;

        public FederatedStudyCancel(ApplicationDbContext db) => _db = db;

        /// <summary>200 with the version view, or 404 study_not_found, 409 task_already_finished, 409 task_busy.</summary>
        public async Task<ObjectResult> CancelAsync(string id, ClaimsPrincipal user, HttpContext http, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var versionId))
            {
                return StudyNotFound();
            }

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var version = await _db.FederatedStudyVersions
                    .Include(x => x.Parts).ThenInclude(x => x.PhaseChanges)
                    .FirstOrDefaultAsync(x => x.VersionId == versionId, cancellationToken);
                if (version == null)
                {
                    return StudyNotFound();
                }

                if (version.Outcome == FederatedStudyOutcome.Canceled)
                {
                    return new OkObjectResult(FederatedStudyVersions.ToView(version));
                }

                if (version.Outcome is { } outcome)
                {
                    return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskAlreadyFinished,
                        $"version {version.Version} of study '{version.StudyId}' already ended: {outcome.ToWire()}");
                }

                FederatedStudyVersions.RequestCancel(version, DateTime.UtcNow);
                FederatedAudit.Add(_db, FiveSafesTes.Core.Models.LogType.FederatedStudyCanceled, user, http,
                    new { versionId = version.VersionId, studyId = version.StudyId, version = version.Version });
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    Log.Information("Federated study version {VersionId} cancelled by {User}: now {State}", versionId,
                        user.FindFirst("preferred_username")?.Value, version.State.ToWire());
                    return new OkObjectResult(FederatedStudyVersions.ToView(version));
                }
                catch (DbUpdateConcurrencyException)
                {
                    // An Agent report moved a part after it was read here: decide again against the phases it left.
                    _db.ChangeTracker.Clear();
                }
            }

            return Error(StatusCodes.Status409Conflict, FederatedTaskErrorCodes.TaskBusy,
                "the study version is being updated by other reports, try again");
        }

        private static ObjectResult StudyNotFound() =>
            Error(StatusCodes.Status404NotFound, FederatedTaskErrorCodes.StudyNotFound, "study or version not found");

        private static ObjectResult Error(int status, string code, string message) =>
            new(new FederatedApiError { ErrorCode = code, ErrorMessage = message }) { StatusCode = status };
    }
}
