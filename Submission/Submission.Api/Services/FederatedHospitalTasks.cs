using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// The work a hospital fetches when it polls: its unfinished parts, most urgent first. The same list for the TES
    /// ListTasks route (with the hospital's login) and the older FederatedTask/GetPendingTasksForTre.
    /// </summary>
    public static class FederatedHospitalTasks
    {
        /// <summary>Upper bound on parts handed to a hospital in one poll.</summary>
        public const int MaxPendingTasks = 50;

        public static async Task<List<FederatedTask>> PendingAsync(ApplicationDbContext db, Tre tre, int max, CancellationToken cancellationToken)
        {
            var treName = tre.Name.ToLower();

            // The Agent refuses tasks whose fed.sites is not its own name and leaves them as they are. Tasks enqueued under a
            // name the TRE no longer has would stay unfinished forever and fill the list, so they are not handed out;
            // except a canceling task, which the Agent cleans up before any name check (the rename sweep skips it too).
            // ToTesTask reads the history. Without the Include, lazy loading would run one query per task.
            return await db.FederatedTasks
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
                .Take(max)
                .ToListAsync(cancellationToken);
        }
    }
}
