using Data_Egress_API.Repositories.DbContexts;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.APISimpleTypeReturns;
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

            try
            {
                await tre.CallAPI<EgressReview, Submission>("/api/Submission/EgressResults", review);
            }
            catch (Exception ex) when (FinalRefusal(ex) is { } status)
            {
                // The Agent will never accept this decision (for example the task was withdrawn, or a different
                // decision is already recorded), so it is closed instead of being sent again every minute.
                row.TreClosedReason = $"The TRE refused this decision (HTTP {(int)status}). It is not sent again.";
                await db.SaveChangesAsync();
                Log.Warning(ex, "{Function} The TRE refused the decision for Submission {SubId} with HTTP {Status}; it is closed",
                    "DeliverFederatedDecision", row.SubmissionId, (int)status);
                throw new EgressRefusedException(row.TreClosedReason);
            }

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

        /// <summary>
        /// A refusal is final only when the Agent's own EgressResults handler marked it so (400, 404 or 409 with
        /// <see cref="FederatedDecisionReply.FinalRefusal"/>): sending the same decision again cannot change it.
        /// The same status from anything else (a proxy, an ingress during a redeploy, a wrong address), network
        /// errors, 5xx and 401/403 from a configuration problem may pass, and are retried.
        /// </summary>
        public static System.Net.HttpStatusCode? FinalRefusal(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AggregateException agg) e = agg.Flatten().GetBaseException();
                if (e is FiveSafesTes.Core.Services.ApiCallException api &&
                    FederatedDecisionReply.IsFinal((int)api.StatusCode) && MarkedFinal(api.ResponseBody))
                    return api.StatusCode;
            }

            return null;
        }

        private static bool MarkedFinal(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                var refusal = json.GetValue(nameof(FederatedDecisionReply.Refusal), StringComparison.OrdinalIgnoreCase);
                return refusal?.ToString() == FederatedDecisionReply.FinalRefusal;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>Sends every saved federated decision that has not reached the Agent; returns how many were sent.</summary>
        public static async Task<int> RedeliverPendingAsync(ApplicationDbContext db, ITreClientWithoutTokenHelper tre,
            CancellationToken ct)
        {
            var pending = await db.EgressSubmissions
                .Where(x => x.Kind == EgressSubmission.FederatedKind && x.Status != EgressStatus.NotCompleted &&
                            !x.DecisionDelivered && x.TreClosedReason == null)
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
                catch (EgressRefusedException)
                {
                    // Refused for good and closed; already logged.
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
