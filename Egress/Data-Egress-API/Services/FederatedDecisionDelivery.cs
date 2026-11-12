using Data_Egress_API.Repositories.DbContexts;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Data_Egress_API.Services
{
    /// <summary>
    /// Sends a saved federated decision to the TRE Agent. A decision is saved before it is sent, so one that could
    /// not be sent is kept and sent again: by the reviewer, or by <see cref="UndeliveredDecisionRetry"/>.
    /// </summary>
    public static class FederatedDecisionDelivery
    {
        public static async Task<EgressSubmission> DeliverAsync(ApplicationDbContext db,
            ITreClientWithoutTokenHelper tre, EgressSubmission row)
        {
            var review = new EgressReview()
            {
                SubId = row.SubmissionId,
                OutputBucket = row.OutputBucket,
                FileResults = row.Files.Select(x => new EgressResult()
                {
                    FileName = x.Name,
                    Approved = x.Status == FileStatus.Approved,
                }).ToList(),
                Reviewer = row.Reviewer,
                DecidedAt = row.Completed,
                Reason = row.DecisionReason,
                ResultSha256 = row.ResultSha256,
            };

            await tre.CallAPI<EgressReview, Submission>("/api/Submission/EgressResults", review);

            row.DecisionDelivered = true;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // A parallel send of the same decision saved it first; the Agent has it either way.
                await db.Entry(row).ReloadAsync();
            }

            Log.Information("{Function} Federated decision delivered for Submission {SubId}", "DeliverFederatedDecision",
                row.SubmissionId);
            return row;
        }

        /// <summary>Sends every saved federated decision that has not reached the Agent; returns how many were sent.</summary>
        public static async Task<int> RedeliverPendingAsync(ApplicationDbContext db, ITreClientWithoutTokenHelper tre,
            CancellationToken ct)
        {
            var pending = await db.EgressSubmissions
                .Where(x => x.Kind == EgressSubmission.FederatedKind && x.Status != EgressStatus.NotCompleted &&
                            !x.DecisionDelivered)
                .OrderBy(x => x.Completed)
                .ToListAsync(ct);

            var sent = 0;
            foreach (var row in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DeliverAsync(db, tre, row);
                    sent++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "{Function} The decision for Submission {SubId} has still not reached the Agent; it is sent again later",
                        "RedeliverPending", row.SubmissionId);
                }
            }

            return sent;
        }
    }

    /// <summary>Once a minute, sends the federated decisions that could not be sent when they were made.</summary>
    public sealed class UndeliveredDecisionRetry : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopes;

        public UndeliveredDecisionRetry(IServiceScopeFactory scopes)
        {
            _scopes = scopes;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var tre = scope.ServiceProvider.GetRequiredService<ITreClientWithoutTokenHelper>();
                    await FederatedDecisionDelivery.RedeliverPendingAsync(db, tre, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "{Function} Crashed", "UndeliveredDecisionRetry");
                }
            }
        }
    }
}
