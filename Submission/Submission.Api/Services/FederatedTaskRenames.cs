using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// Ends federated tasks that a TRE rename has orphaned.
    /// </summary>
    public static class FederatedTaskRenames
    {
        /// <summary>
        /// Marks the TRE's QUEUED tasks that were enqueued under a name other than <paramref name="treName"/> as
        /// SYSTEM_ERROR / tre_renamed. The Agent would refuse them and they would never run. Changes are tracked,
        /// not saved, so the caller saves them together with the rename.
        /// </summary>
        /// <returns>The number of tasks failed.</returns>
        public static int FailQueuedUnderOtherNames(ApplicationDbContext db, int treId, string treName)
        {
            var current = treName.Trim().ToLower();
            var orphaned = db.FederatedTasks
                .Where(x => x.TreId == treId && x.State == TesState.QUEUEDEnum && x.TreName != current)
                .ToList();

            var now = DateTime.UtcNow;
            foreach (var task in orphaned)
            {
                task.State = TesState.SYSTEMERROREnum;
                task.CompletedAt = now;
                task.ErrorCode = FederatedTaskErrorCodes.TreRenamed;
                task.ResultErrorMessage = $"the TRE was renamed to '{current}' before this task ran; enqueue it again under the new name";
            }

            return orphaned.Count;
        }
    }
}
