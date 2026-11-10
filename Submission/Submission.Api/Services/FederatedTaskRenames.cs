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
                .Where(x => x.TreId == treId && x.TreName != current)
                .Where(FederatedTaskSteps.IsUnfinishedTask)
                .Include(x => x.StepChanges)
                .ToList();

            var now = DateTime.UtcNow;
            foreach (var task in orphaned)
            {
                task.Fail(now, TesState.SYSTEMERROREnum, FederatedTaskErrorCodes.TreRenamed,
                    $"the TRE was renamed to '{current}' before this task finished; enqueue it again under the new name");
            }

            return orphaned.Count;
        }

        // An Agent reports each task at most a few times per poll, so a third conflict in a row is not expected.
        private const int MaxSaveAttempts = 3;

        /// <summary>
        /// Runs <see cref="FailUnfinishedUnderOtherNames"/> and saves it together with the caller's other tracked
        /// changes (the rename). An Agent report can change a swept task between the read and the save. Step is a
        /// concurrency token, so that save fails; the swept tasks are then read again and the sweep is redone. A
        /// task the report finished is left as it is.
        /// </summary>
        /// <returns>The number of tasks failed.</returns>
        public static async Task<int> SaveWithSweepAsync(ApplicationDbContext db, int treId, string treName)
        {
            for (var attempt = 1; ; attempt++)
            {
                var failed = FailUnfinishedUnderOtherNames(db, treId, treName);
                try
                {
                    await db.SaveChangesAsync();
                    return failed;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
                {
                    // Forget the sweep's stale tasks and history; the rename itself stays tracked for the next save.
                    foreach (var entry in db.ChangeTracker.Entries()
                                 .Where(e => e.Entity is FederatedTask or FederatedTaskStepChange).ToList())
                    {
                        entry.State = EntityState.Detached;
                    }
                }
            }
        }
    }
}
