using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.EntityFrameworkCore;
using Submission.Api.Repositories.DbContexts;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedStudyVersionTests
    {
        private static readonly DateTime Submitted = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

        private static FederatedStudyVersion VersionWith(FederatedStudyOutcome? outcome, params FederatedTaskPhase[] phases) => new()
        {
            SubmittedAt = Submitted,
            DeadlineAt = Submitted.AddDays(7),
            Outcome = outcome,
            Parts = phases.Select(p => new FederatedTask { Phase = p }).ToList()
        };

        [Theory]
        [InlineData(null, FederatedStudyVersionState.Open, FederatedTaskPhase.Released, FederatedTaskPhase.Failed)]
        [InlineData(FederatedStudyOutcome.Incomplete, FederatedStudyVersionState.Canceling, FederatedTaskPhase.Canceled, FederatedTaskPhase.Canceling)]
        [InlineData(FederatedStudyOutcome.ReadyForAggregation, FederatedStudyVersionState.Ended, FederatedTaskPhase.Released, FederatedTaskPhase.Released)]
        public void State_FollowsOutcomeAndParts(FederatedStudyOutcome? outcome, FederatedStudyVersionState expected, params FederatedTaskPhase[] phases)
        {
            Assert.Equal(expected, VersionWith(outcome, phases).State);
        }

        public static TheoryData<bool, FederatedTaskPhase[], FederatedStudyOutcome?, string?, FederatedTaskPhase[]> DecideCases => new()
        {
            // Both release.
            { false, [FederatedTaskPhase.Released, FederatedTaskPhase.Released],
                FederatedStudyOutcome.ReadyForAggregation, "all_final", [FederatedTaskPhase.Released, FederatedTaskPhase.Released] },
            // One rejects at output approval.
            { false, [FederatedTaskPhase.Released, FederatedTaskPhase.RejectedAtOutputApproval],
                FederatedStudyOutcome.Incomplete, "minimum_unreachable", [FederatedTaskPhase.Released, FederatedTaskPhase.RejectedAtOutputApproval] },
            // Early end: two rejected, the third can't make 2 on its own.
            { false, [FederatedTaskPhase.RejectedByInputChecks, FederatedTaskPhase.RejectedAtOutputApproval, FederatedTaskPhase.WaitingForOutputApproval],
                FederatedStudyOutcome.Incomplete, "minimum_unreachable",
                [FederatedTaskPhase.RejectedByInputChecks, FederatedTaskPhase.RejectedAtOutputApproval, FederatedTaskPhase.Canceling] },
            // Early end with a part no hospital picked up.
            { false, [FederatedTaskPhase.Failed, FederatedTaskPhase.Queued],
                FederatedStudyOutcome.Incomplete, "minimum_unreachable", [FederatedTaskPhase.Failed, FederatedTaskPhase.Canceled] },
            // Timeout with the minimum met.
            { true, [FederatedTaskPhase.Released, FederatedTaskPhase.Released, FederatedTaskPhase.WaitingForOutputApproval],
                FederatedStudyOutcome.ReadyForAggregation, "timeout",
                [FederatedTaskPhase.Released, FederatedTaskPhase.Released, FederatedTaskPhase.Canceling] },
            // Timeout below the minimum.
            { true, [FederatedTaskPhase.Released, FederatedTaskPhase.Queued],
                FederatedStudyOutcome.Incomplete, "timeout_below_minimum", [FederatedTaskPhase.Released, FederatedTaskPhase.Canceled] },
            // 2 released, one still working, before the deadline: wait.
            { false, [FederatedTaskPhase.Released, FederatedTaskPhase.Released, FederatedTaskPhase.Running],
                null, null, [FederatedTaskPhase.Released, FederatedTaskPhase.Released, FederatedTaskPhase.Running] },
            // 1 released, one still working, before the deadline: wait.
            { false, [FederatedTaskPhase.Released, FederatedTaskPhase.Running],
                null, null, [FederatedTaskPhase.Released, FederatedTaskPhase.Running] },
        };

        [Theory]
        [MemberData(nameof(DecideCases))]
        public void Decide_follows_the_two_hospital_minimum_and_the_deadline(bool deadlinePassed, FederatedTaskPhase[] before,
            FederatedStudyOutcome? outcome, string? reason, FederatedTaskPhase[] after)
        {
            var version = VersionWith(null, before);
            var now = deadlinePassed ? version.DeadlineAt : version.DeadlineAt.AddMinutes(-1);

            var decided = FederatedStudyOutcomes.Decide(version, now);

            Assert.Equal(outcome is not null, decided);
            Assert.Equal(outcome, version.Outcome);
            Assert.Equal(reason, version.OutcomeReason);
            Assert.Equal(after, version.Parts.Select(p => p.Phase));
            Assert.Equal(outcome is null ? null : now, version.OutcomeAt);
            Assert.Equal(outcome == FederatedStudyOutcome.ReadyForAggregation ? now : null, version.ReadyAt);
            if (reason is "minimum_unreachable" or "timeout_below_minimum")
            {
                Assert.Contains("at least 2", version.OutcomeMessage);
            }
            if (reason == "minimum_unreachable")
            {
                // A released hospital has not "still" to release: the count covers both.
                Assert.Contains("released or can still release", version.OutcomeMessage);
            }
        }

        [Theory]
        [InlineData(FederatedStudyOutcome.ReadyForAggregation, 1440, true)]
        [InlineData(FederatedStudyOutcome.ReadyForAggregation, 1439, false)]
        [InlineData(FederatedStudyOutcome.Aggregated, 2000, false)]
        [InlineData(FederatedStudyOutcome.Incomplete, 2000, false)]
        public void TimeOutAggregation_fails_only_ready_versions_past_the_limit(FederatedStudyOutcome outcome, int minutesReady, bool timedOut)
        {
            var version = VersionWith(outcome, FederatedTaskPhase.Released, FederatedTaskPhase.Released);
            var now = Submitted.AddDays(1);
            version.ReadyAt = now.AddMinutes(-minutesReady);

            Assert.Equal(timedOut, FederatedStudyOutcomes.TimeOutAggregation(version, now, 1440));

            Assert.Equal(timedOut ? FederatedStudyOutcome.Failed : outcome, version.Outcome);
            Assert.Equal(timedOut ? "aggregation_timeout" : null, version.AggregationErrorCode);
            Assert.Equal(timedOut ? now : null, version.OutcomeAt);
        }

        [Fact]
        public void Decide_changes_nothing_once_there_is_an_outcome()
        {
            var version = VersionWith(FederatedStudyOutcome.Canceled, FederatedTaskPhase.Released, FederatedTaskPhase.Canceling);

            Assert.False(FederatedStudyOutcomes.Decide(version, version.DeadlineAt.AddDays(1)));
            Assert.Equal(FederatedStudyOutcome.Canceled, version.Outcome);
            Assert.Null(version.OutcomeAt);
        }

        [Fact]
        public void TesTaskJson_CannotChangeAfterSave()
        {
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var version = new FederatedStudyVersion { VersionId = Guid.NewGuid(), StudyId = "s", Version = 1, TesTaskJson = "{}" };
            db.FederatedStudyVersions.Add(version);
            db.SaveChanges();

            db.Entry(version).Property(x => x.TesTaskJson).CurrentValue = "{\"x\":1}";

            var error = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            Assert.Contains("TesTaskJson", error.Message);
        }
    }
}
