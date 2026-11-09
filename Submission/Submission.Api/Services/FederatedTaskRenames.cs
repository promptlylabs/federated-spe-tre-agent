using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.EntityFrameworkCore;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// Ends federated tasks that a TRE rename has orphaned.
    /// </summary>
    public static class FederatedTaskRenames
    {
        /// <summary>
        /// Marks the TRE's unfinished (queued, running or waiting) tasks that were enqueued under a name other than <paramref name="treName"/> as
        /// failed (SYSTEM_ERROR / tre_renamed). The Agent would refuse them and they would never finish. Changes are tracked,
        /// not saved, so the caller saves them together with the rename.
        /// </summary>
        /// <returns>The number of tasks failed.</returns>
        public static int FailUnfinishedUnderOtherNames(ApplicationDbContext db, int treId, string treName)
        {
            var current = treName.Trim().ToLower();
            var orphaned = db.FederatedTasks
                .Where(x => x.TreId == treId && x.TreName != current
                    && (x.Step == FederatedTaskStep.Queued || x.Step == FederatedTaskStep.Running || x.Step == FederatedTaskStep.WaitingForOutputApproval))
                .Include(x => x.StepChanges)
                .ToList();

            var now = DateTime.UtcNow;
            foreach (var task in orphaned)
            {
                task.MoveTo(FederatedTaskStep.Failed, now, TesState.SYSTEMERROREnum, FederatedTaskErrorCodes.TreRenamed,
                    $"the TRE was renamed to '{current}' before this task ran; enqueue it again under the new name");
            }

            return orphaned.Count;
        }
    }
}
