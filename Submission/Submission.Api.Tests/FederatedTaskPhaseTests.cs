using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedTaskPhaseTests
    {
        private static readonly DateTime T0 = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

        private static int Rank(FederatedTaskPhase s) => s switch
        {
            FederatedTaskPhase.Queued => 0,
            FederatedTaskPhase.Running => 1,
            FederatedTaskPhase.WaitingForOutputApproval => 2,
            FederatedTaskPhase.Canceling => 3,
            FederatedTaskPhase.Released or FederatedTaskPhase.RejectedByInputChecks
                or FederatedTaskPhase.RejectedAtOutputApproval or FederatedTaskPhase.Failed or FederatedTaskPhase.Canceled => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s, "add the new phase to this test")
        };

        public static IEnumerable<object[]> AllPairs() =>
            from c in Enum.GetValues<FederatedTaskPhase>()
            from n in Enum.GetValues<FederatedTaskPhase>()
            select new object[] { c, n };

        /// <summary>
        /// The phase graph. A cancel is the only way into canceling (from running or waiting) and into canceled (from
        /// queued, or canceling once the hospital confirms); a canceling task can only end canceled.
        /// </summary>
        private static FederatedTaskTransition Expected(FederatedTaskPhase current, FederatedTaskPhase next)
        {
            if (Rank(current) == 4)
                return current == next ? FederatedTaskTransition.NoChange : FederatedTaskTransition.AlreadyFinal;
            if (current == FederatedTaskPhase.Canceling)
                return next switch
                {
                    FederatedTaskPhase.Canceled => FederatedTaskTransition.Apply,
                    FederatedTaskPhase.Canceling => FederatedTaskTransition.NoChange,
                    _ => FederatedTaskTransition.Canceling
                };
            if (next == FederatedTaskPhase.Canceling)
                return current is FederatedTaskPhase.Running or FederatedTaskPhase.WaitingForOutputApproval
                    ? FederatedTaskTransition.Apply : FederatedTaskTransition.Invalid;
            if (next == FederatedTaskPhase.Canceled)
                return current == FederatedTaskPhase.Queued ? FederatedTaskTransition.Apply : FederatedTaskTransition.Invalid;
            return Rank(next) > Rank(current) ? FederatedTaskTransition.Apply
                : Rank(next) == Rank(current) ? FederatedTaskTransition.NoChange
                : FederatedTaskTransition.Backwards;
        }

        [Theory]
        [MemberData(nameof(AllPairs))]
        public void Check_table(FederatedTaskPhase current, FederatedTaskPhase next)
        {
            var currentFinal = Rank(current) == 4;
            var expected = Expected(current, next);

            Assert.Equal(expected, FederatedTaskPhases.Check(current, next));
            Assert.Equal(currentFinal, FederatedTaskPhases.IsFinal(current));
        }

        [Fact]
        public void ToWire_roundtrips_every_phase()
        {
            foreach (var phase in Enum.GetValues<FederatedTaskPhase>())
            {
                Assert.True(FederatedTaskPhases.TryParse(FederatedTaskPhases.ToWire(phase), out var parsed));
                Assert.Equal(phase, parsed);
            }

            Assert.Equal("waiting_for_output_approval", FederatedTaskPhases.ToWire(FederatedTaskPhase.WaitingForOutputApproval));
        }

        [Theory]
        [InlineData("Running")]
        [InlineData("RUNNING")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("nope")]
        public void TryParse_rejects_unknown_and_case_variants(string? value)
        {
            Assert.False(FederatedTaskPhases.TryParse(value, out _));
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Queued, TesState.QUEUEDEnum)]
        [InlineData(FederatedTaskPhase.Running, TesState.RUNNINGEnum)]
        [InlineData(FederatedTaskPhase.WaitingForOutputApproval, TesState.RUNNINGEnum)]
        [InlineData(FederatedTaskPhase.Released, TesState.COMPLETEEnum)]
        [InlineData(FederatedTaskPhase.Canceling, TesState.CANCELINGEnum)]
        [InlineData(FederatedTaskPhase.Canceled, TesState.CANCELEDEnum)]
        public void MoveTo_sets_tes_state(FederatedTaskPhase phase, TesState expected)
        {
            var task = new FederatedTask();
            task.MoveTo(phase, T0);
            Assert.Equal(phase, task.Phase);
            Assert.Equal(expected, task.State);
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Failed)]
        [InlineData(FederatedTaskPhase.RejectedByInputChecks)]
        [InlineData(FederatedTaskPhase.RejectedAtOutputApproval)]
        public void MoveTo_refuses_phases_that_need_a_reason(FederatedTaskPhase phase)
        {
            Assert.Throws<ArgumentException>(() => new FederatedTask().MoveTo(phase, T0));
        }

        [Theory]
        [InlineData(FederatedTaskPhase.RejectedByInputChecks)]
        [InlineData(FederatedTaskPhase.RejectedAtOutputApproval)]
        public void Reject_sets_canceled_and_keeps_the_reason(FederatedTaskPhase phase)
        {
            var task = new FederatedTask();
            task.Reject(phase, T0, "output_rejected", "too few records");
            Assert.Equal(phase, task.Phase);
            Assert.Equal(TesState.CANCELEDEnum, task.State);
            Assert.Equal("output_rejected", task.ErrorCode);
            Assert.Equal("too few records", task.ResultErrorMessage);
            Assert.Equal(T0, task.CompletedAt);
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Queued)]
        [InlineData(FederatedTaskPhase.Released)]
        [InlineData(FederatedTaskPhase.Failed)]
        public void Reject_refuses_phases_that_are_not_rejections(FederatedTaskPhase phase)
        {
            Assert.Throws<ArgumentException>(() => new FederatedTask().Reject(phase, T0, "x", "y"));
        }

        [Theory]
        [InlineData(TesState.EXECUTORERROREnum)]
        [InlineData(TesState.SYSTEMERROREnum)]
        public void Fail_keeps_the_reported_state_and_reason(TesState state)
        {
            var task = new FederatedTask();
            task.Fail(T0, state, "internal_error", "boom");
            Assert.Equal(FederatedTaskPhase.Failed, task.Phase);
            Assert.Equal(state, task.State);
            Assert.Equal("internal_error", task.ErrorCode);
            Assert.Equal("boom", task.ResultErrorMessage);
        }

        [Theory]
        [InlineData(TesState.COMPLETEEnum)]
        [InlineData(TesState.CANCELEDEnum)]
        [InlineData(TesState.QUEUEDEnum)]
        public void Fail_refuses_states_that_are_not_errors(TesState state)
        {
            Assert.Throws<ArgumentException>(() => new FederatedTask().Fail(T0, state, "x", "y"));
        }

        [Fact]
        public void IsUnfinishedTask_query_agrees_with_IsFinal_for_every_phase()
        {
            var isUnfinished = FederatedTaskPhases.IsUnfinishedTask.Compile();
            foreach (var phase in Enum.GetValues<FederatedTaskPhase>())
            {
                Assert.Equal(!FederatedTaskPhases.IsFinal(phase), isUnfinished(new FederatedTask { Phase = phase }));
            }
        }

        [Fact]
        public void MoveTo_final_sets_CompletedAt_and_appends_history()
        {
            var task = new FederatedTask();
            task.MoveTo(FederatedTaskPhase.Queued, T0);
            Assert.Null(task.CompletedAt);

            task.MoveTo(FederatedTaskPhase.Released, T0.AddMinutes(5));

            Assert.Equal(T0.AddMinutes(5), task.CompletedAt);
            Assert.Equal(new[] { FederatedTaskPhase.Queued, FederatedTaskPhase.Released }, task.PhaseChanges.Select(c => c.Phase).ToArray());
        }

        private static FederatedTask NewTask()
        {
            return new FederatedTask { TaskId = Guid.NewGuid(), SubmittedAt = T0, TesTaskJson = "{}" };
        }

        [Fact]
        public void PhaseOf_reads_the_phase_from_the_task_log()
        {
            var task = NewTask();
            task.MoveTo(FederatedTaskPhase.Queued, T0);
            task.MoveTo(FederatedTaskPhase.WaitingForOutputApproval, T0.AddMinutes(1));

            Assert.Equal(FederatedTaskPhase.WaitingForOutputApproval, FederatedTaskPhases.PhaseOf(task.ToTesTask()));
        }

        [Fact]
        public void PhaseOf_is_null_for_a_task_without_a_phase() =>
            Assert.Null(FederatedTaskPhases.PhaseOf(new TesTask()));

        [Fact]
        public void ToTesTask_waiting_shows_phase_and_times()
        {
            var task = NewTask();
            task.MoveTo(FederatedTaskPhase.Queued, T0);
            task.MoveTo(FederatedTaskPhase.Running, T0.AddMinutes(1));
            task.MoveTo(FederatedTaskPhase.WaitingForOutputApproval, T0.AddMinutes(2));

            var tes = task.ToTesTask();
            var log = Assert.Single(tes.Logs!);

            Assert.Equal(TesState.RUNNINGEnum, tes.State);
            Assert.Equal("waiting_for_output_approval", log.Metadata["phase"]);
            Assert.Equal("2026-11-09T10:00:00.0000000Z", log.Metadata["queued_at"]);
            Assert.True(log.Metadata.ContainsKey("running_at"));
            Assert.True(log.Metadata.ContainsKey("waiting_for_output_approval_at"));
            Assert.Equal(new DateTimeOffset(T0.AddMinutes(1)), log.StartTime);
            Assert.Null(log.EndTime);
        }

        [Fact]
        public void ToTesTask_rejection_shows_reason()
        {
            var task = NewTask();
            task.MoveTo(FederatedTaskPhase.Queued, T0);
            task.Reject(FederatedTaskPhase.RejectedByInputChecks, T0.AddMinutes(1), "dataset_not_found", "dataset 'x' not found");

            var tes = task.ToTesTask();
            var m = tes.Logs![0].Metadata;

            Assert.Equal(TesState.CANCELEDEnum, tes.State);
            Assert.Equal("rejected_by_input_checks", m["phase"]);
            Assert.Equal("dataset_not_found", m["error_code"]);
            Assert.Equal("dataset 'x' not found", m["error_message"]);
            Assert.True(m.ContainsKey("rejected_by_input_checks_at"));
        }

        [Fact]
        public void ToTesTask_released_keeps_result_keys()
        {
            var task = NewTask();
            task.ResultBucket = "b";
            task.ResultKey = "k";
            task.ResultSha256 = new string('a', 64);
            task.MoveTo(FederatedTaskPhase.Queued, T0);
            task.MoveTo(FederatedTaskPhase.Released, T0.AddMinutes(3));

            var log = task.ToTesTask().Logs![0];

            Assert.Equal("b", log.Metadata["result_bucket"]);
            Assert.Equal("k", log.Metadata["result_key"]);
            Assert.Equal(new string('a', 64), log.Metadata["result_sha256"]);
            Assert.True(log.Metadata.ContainsKey("released_at"));
            Assert.Equal(new DateTimeOffset(T0.AddMinutes(3)), log.EndTime);
        }
    }
}
