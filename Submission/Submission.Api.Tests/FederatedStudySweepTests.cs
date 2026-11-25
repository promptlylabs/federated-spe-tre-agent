using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedStudySweepTests
    {
        private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        private readonly string _name = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot _root = new();

        private ApplicationDbContext Open(params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_name, _root).AddInterceptors(interceptors).Options);

        /// <summary>Seeds a version with one part per phase (a part reaches its phase through running or the input checks).</summary>
        private Guid Seed(bool deadlinePassed, FederatedStudyOutcome? outcome, params FederatedTaskPhase[] phases)
        {
            using var db = Open();
            if (!db.Tres.Any())
            {
                db.Tres.Add(new Tre { Id = 1, Name = "tre-a", AdminUsername = "a", KeycloakClientId = "c", About = "", FormData = "" });
            }

            var submitted = Now.AddDays(-1);
            var version = new FederatedStudyVersion
            {
                VersionId = Guid.NewGuid(),
                StudyId = $"study-{Guid.NewGuid():N}",
                Version = 1,
                TesTaskJson = "{}",
                SubmittedAt = submitted,
                DeadlineAt = deadlinePassed ? Now.AddMinutes(-1) : Now.AddDays(6),
                Outcome = outcome
            };
            foreach (var phase in phases)
            {
                var part = new FederatedTask { TaskId = Guid.NewGuid(), TreId = 1, TreName = "tre-a", StudyId = version.StudyId, TesTaskJson = "{}", SubmittedAt = submitted };
                part.MoveTo(FederatedTaskPhase.Queued, submitted);
                switch (phase)
                {
                    case FederatedTaskPhase.Queued:
                        break;
                    case FederatedTaskPhase.RejectedByInputChecks:
                        part.Reject(phase, submitted, FederatedTaskErrorCodes.DatasetNotFound, "no dataset");
                        break;
                    default:
                        part.MoveTo(FederatedTaskPhase.Running, submitted);
                        if (phase == FederatedTaskPhase.RejectedAtOutputApproval) part.Reject(phase, submitted, FederatedTaskErrorCodes.RejectedByReviewer, "small cells");
                        else if (phase == FederatedTaskPhase.Canceling) part.RequestCancel(submitted);
                        else if (phase == FederatedTaskPhase.Failed) part.Fail(submitted, FiveSafesTes.Core.Models.Tes.TesState.EXECUTORERROREnum, FederatedTaskErrorCodes.RunFailed, "boom");
                        else if (phase != FederatedTaskPhase.Running) part.MoveTo(phase, submitted);
                        break;
                }
                version.Parts.Add(part);
            }

            db.FederatedStudyVersions.Add(version);
            db.SaveChanges();
            return version.VersionId;
        }

        private FederatedStudyVersion Load(Guid versionId)
        {
            using var db = Open();
            return db.FederatedStudyVersions.Include(x => x.Parts).Single(x => x.VersionId == versionId);
        }

        [Fact]
        public async Task Sweep_decides_undecided_versions_and_leaves_the_rest()
        {
            var rejected = Seed(false, null, FederatedTaskPhase.Released, FederatedTaskPhase.RejectedAtOutputApproval);
            var waiting = Seed(false, null, FederatedTaskPhase.Released, FederatedTaskPhase.Running);
            var canceled = Seed(false, FederatedStudyOutcome.Canceled, FederatedTaskPhase.Released, FederatedTaskPhase.Canceling);
            using var db = Open();

            var decided = await FederatedStudySweep.SweepOnceAsync(db, Now, CancellationToken.None);

            Assert.Equal(1, decided);
            var stored = Load(rejected);
            Assert.Equal((FederatedStudyOutcome.Incomplete, "minimum_unreachable"), (stored.Outcome, stored.OutcomeReason));
            Assert.Null(Load(waiting).Outcome);
            Assert.Null(Load(canceled).OutcomeReason);
        }

        [Fact]
        public async Task Sweep_cancels_open_parts_at_the_deadline()
        {
            var versionId = Seed(true, null, FederatedTaskPhase.Released, FederatedTaskPhase.Queued);
            using var db = Open();

            await FederatedStudySweep.SweepOnceAsync(db, Now, CancellationToken.None);

            var stored = Load(versionId);
            Assert.Equal((FederatedStudyOutcome.Incomplete, "timeout_below_minimum"), (stored.Outcome, stored.OutcomeReason));
            Assert.Equal(new[] { FederatedTaskPhase.Released, FederatedTaskPhase.Canceled }, stored.Parts.OrderBy(p => p.Id).Select(p => p.Phase));
        }

        [Fact]
        public async Task Sweep_leaves_a_version_whose_part_moved_meanwhile()
        {
            var versionId = Seed(true, null, FederatedTaskPhase.Released, FederatedTaskPhase.Running);
            // The hospital reports between the sweep's read and its save.
            var report = new BeforeFirstSave(() =>
            {
                using var other = Open();
                other.FederatedTasks.Include(x => x.PhaseChanges).Single(x => x.Phase == FederatedTaskPhase.Running)
                    .MoveTo(FederatedTaskPhase.WaitingForOutputApproval, Now);
                other.SaveChanges();
            });
            using var db = Open(report);

            var decided = await FederatedStudySweep.SweepOnceAsync(db, Now, CancellationToken.None);

            // The hospital's report stands: the sweep's cancel lost on the part's Phase token. (On Postgres the whole save
            // rolls back, so the version has no outcome either and the next sweep decides it; the in-memory provider is not
            // transactional, so that half is not checked here.)
            Assert.Equal(0, decided);
            Assert.Contains(Load(versionId).Parts, p => p.Phase == FederatedTaskPhase.WaitingForOutputApproval);
            Assert.DoesNotContain(Load(versionId).Parts, p => p.Phase == FederatedTaskPhase.Canceling);
        }

        [Fact]
        public async Task Sweep_goes_on_after_one_version_fails()
        {
            var first = Seed(false, null, FederatedTaskPhase.Released, FederatedTaskPhase.Failed);
            var second = Seed(false, null, FederatedTaskPhase.Released, FederatedTaskPhase.Released);
            using var db = Open(new BeforeFirstSave(() => throw new InvalidOperationException("database went away")));

            var decided = await FederatedStudySweep.SweepOnceAsync(db, Now, CancellationToken.None);

            Assert.Equal(1, decided);
            Assert.Null(Load(first).Outcome);
            Assert.Equal(FederatedStudyOutcome.ReadyForAggregation, Load(second).Outcome);
        }

        private sealed class BeforeFirstSave(Action competingWrite) : SaveChangesInterceptor
        {
            private bool _done;

            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (!_done)
                {
                    _done = true;
                    competingWrite();
                }

                return ValueTask.FromResult(result);
            }
        }
    }
}
