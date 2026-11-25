using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// Decides the outcome of every study version that has none yet, at startup and then every
    /// <see cref="FederatedTaskSettings.StudySweepSeconds"/>. A hospital that goes silent sends nothing, so something has to
    /// wake up and look at the clock; deciding only here keeps the hospital report routes free of study logic.
    /// </summary>
    public sealed class FederatedStudySweep(IServiceScopeFactory scopes, FederatedTaskSettings settings) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.StudySweepSeconds));
            try
            {
                // Sweep before the first wait: deadlines that passed while Submission was down are decided at once.
                do
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        await SweepOnceAsync(db, DateTime.UtcNow, settings.AggregationTimeoutMinutes, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        Log.Error(ex, "Federated study sweep failed; trying again at the next tick");
                    }
                } while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
        }

        /// <summary>
        /// Decides each undecided version, and fails each version that has waited for the aggregator longer than
        /// <paramref name="aggregationTimeoutMinutes"/>, saving each on its own. A version that can't be saved (an Agent
        /// report, the aggregator or an admin changed it meanwhile, or the save failed) is left for the next sweep.
        /// </summary>
        /// <returns>The number of versions changed.</returns>
        public static async Task<int> SweepOnceAsync(ApplicationDbContext db, DateTime nowUtc, int aggregationTimeoutMinutes, CancellationToken ct)
        {
            return await DecideAsync(db, nowUtc, ct) + await TimeOutAggregationsAsync(db, nowUtc, aggregationTimeoutMinutes, ct);
        }

        private static async Task<int> TimeOutAggregationsAsync(ApplicationDbContext db, DateTime nowUtc, int timeoutMinutes, CancellationToken ct)
        {
            var cutoff = nowUtc.AddMinutes(-timeoutMinutes);
            var ids = await db.FederatedStudyVersions
                .Where(x => x.Outcome == FederatedStudyOutcome.ReadyForAggregation && x.ReadyAt <= cutoff)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);

            var failed = 0;
            foreach (var id in ids)
            {
                try
                {
                    var version = await db.FederatedStudyVersions.SingleAsync(x => x.Id == id, ct);
                    if (!FederatedStudyOutcomes.TimeOutAggregation(version, nowUtc, timeoutMinutes))
                    {
                        continue;
                    }

                    await db.SaveChangesAsync(ct);
                    failed++;
                    Log.Warning("Federated study version {VersionId} ({StudyId} v{Version}) was not pooled within {Minutes} minutes: failed, aggregation_timeout",
                        version.VersionId, version.StudyId, version.Version, timeoutMinutes);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (DbUpdateConcurrencyException)
                {
                    Log.Information("Federated study version {Id} changed while its aggregation was timed out; the next sweep checks it again", id);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not time out the aggregation of federated study version {Id}; the next sweep tries again", id);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }

            return failed;
        }

        private static async Task<int> DecideAsync(ApplicationDbContext db, DateTime nowUtc, CancellationToken ct)
        {
            // One read of every undecided version with its parts' phases. Most versions just keep waiting, so only those
            // Decide would change are loaded again, tracked, to be decided and saved.
            var undecided = await db.FederatedStudyVersions
                .AsNoTracking()
                .Where(x => x.Outcome == null)
                .Include(x => x.Parts)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            var ids = undecided.Where(v => FederatedStudyOutcomes.Decide(v, nowUtc)).Select(v => v.Id).ToList();

            var decided = 0;
            foreach (var id in ids)
            {
                try
                {
                    var version = await db.FederatedStudyVersions
                        .Include(x => x.Parts)
                        .SingleAsync(x => x.Id == id, ct);
                    var unfinished = version.Parts.Count(p => !FederatedTaskPhases.IsFinal(p.Phase));
                    if (!FederatedStudyOutcomes.Decide(version, nowUtc))
                    {
                        continue;
                    }

                    await db.SaveChangesAsync(ct);
                    decided++;
                    Log.Information(
                        "Federated study version {VersionId} ({StudyId} v{Version}) is {Outcome} ({Reason}): {Released} released, {Canceled} hospital task(s) cancelled",
                        version.VersionId, version.StudyId, version.Version, version.Outcome!.Value.ToWire(), version.OutcomeReason,
                        version.Parts.Count(p => p.Phase == FederatedTaskPhase.Released), unfinished);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (DbUpdateConcurrencyException)
                {
                    Log.Information("Federated study version {Id} changed while it was being decided; the next sweep decides it", id);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not decide federated study version {Id}; the next sweep tries again", id);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }

            return decided;
        }
    }
}
