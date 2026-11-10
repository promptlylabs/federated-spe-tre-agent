using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedTaskStepTests
    {
        private static readonly DateTime T0 = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

        private static int Rank(FederatedTaskStep s) => s switch
        {
            FederatedTaskStep.Queued => 0,
            FederatedTaskStep.Running => 1,
            FederatedTaskStep.WaitingForOutputApproval => 2,
            FederatedTaskStep.Released or FederatedTaskStep.RejectedByInputChecks
                or FederatedTaskStep.RejectedAtOutputApproval or FederatedTaskStep.Failed => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s, "add the new step to this test")
        };

        public static IEnumerable<object[]> AllPairs() =>
            from c in Enum.GetValues<FederatedTaskStep>()
            from n in Enum.GetValues<FederatedTaskStep>()
            select new object[] { c, n };

        [Theory]
        [MemberData(nameof(AllPairs))]
        public void Check_table(FederatedTaskStep current, FederatedTaskStep next)
        {
            var currentFinal = Rank(current) == 3;
            var expected =
                currentFinal ? (current == next ? FederatedTaskTransition.NoChange : FederatedTaskTransition.AlreadyFinal)
                : Rank(next) > Rank(current) ? FederatedTaskTransition.Apply
                : Rank(next) == Rank(current) ? FederatedTaskTransition.NoChange
                : FederatedTaskTransition.Backwards;

            Assert.Equal(expected, FederatedTaskSteps.Check(current, next));
            Assert.Equal(currentFinal, FederatedTaskSteps.IsFinal(current));
        }

        [Fact]
        public void ToWire_roundtrips_every_step()
        {
            foreach (var step in Enum.GetValues<FederatedTaskStep>())
            {
                Assert.True(FederatedTaskSteps.TryParse(FederatedTaskSteps.ToWire(step), out var parsed));
                Assert.Equal(step, parsed);
            }

            Assert.Equal("waiting_for_output_approval", FederatedTaskSteps.ToWire(FederatedTaskStep.WaitingForOutputApproval));
        }

        [Theory]
        [InlineData("Running")]
        [InlineData("RUNNING")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("nope")]
        public void TryParse_rejects_unknown_and_case_variants(string? value)
        {
            Assert.False(FederatedTaskSteps.TryParse(value, out _));
        }

        [Theory]
        [InlineData(FederatedTaskStep.Queued, TesState.QUEUEDEnum)]
        [InlineData(FederatedTaskStep.Running, TesState.RUNNINGEnum)]
        [InlineData(FederatedTaskStep.WaitingForOutputApproval, TesState.PAUSEDEnum)]
        [InlineData(FederatedTaskStep.Released, TesState.COMPLETEEnum)]
        public void MoveTo_sets_tes_state(FederatedTaskStep step, TesState expected)
        {
            var task = new FederatedTask();
            task.MoveTo(step, T0);
            Assert.Equal(step, task.Step);
            Assert.Equal(expected, task.State);
        }

        [Theory]
        [InlineData(FederatedTaskStep.Failed)]
        [InlineData(FederatedTaskStep.RejectedByInputChecks)]
        [InlineData(FederatedTaskStep.RejectedAtOutputApproval)]
        public void MoveTo_refuses_steps_that_need_a_reason(FederatedTaskStep step)
        {
            Assert.Throws<ArgumentException>(() => new FederatedTask().MoveTo(step, T0));
        }

        [Theory]
        [InlineData(FederatedTaskStep.RejectedByInputChecks)]
        [InlineData(FederatedTaskStep.RejectedAtOutputApproval)]
        public void Reject_sets_canceled_and_keeps_the_reason(FederatedTaskStep step)
        {
            var task = new FederatedTask();
            task.Reject(step, T0, "output_rejected", "too few records");
            Assert.Equal(step, task.Step);
            Assert.Equal(TesState.CANCELEDEnum, task.State);
            Assert.Equal("output_rejected", task.ErrorCode);
            Assert.Equal("too few records", task.ResultErrorMessage);
            Assert.Equal(T0, task.CompletedAt);
        }

        [Theory]
        [InlineData(FederatedTaskStep.Queued)]
        [InlineData(FederatedTaskStep.Released)]
        [InlineData(FederatedTaskStep.Failed)]
        public void Reject_refuses_steps_that_are_not_rejections(FederatedTaskStep step)
        {
            Assert.Throws<ArgumentException>(() => new FederatedTask().Reject(step, T0, "x", "y"));
        }

        [Theory]
        [InlineData(TesState.EXECUTORERROREnum)]
        [InlineData(TesState.SYSTEMERROREnum)]
        public void Fail_keeps_the_reported_state_and_reason(TesState state)
        {
            var task = new FederatedTask();
            task.Fail(T0, state, "internal_error", "boom");
            Assert.Equal(FederatedTaskStep.Failed, task.Step);
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
        public void IsUnfinishedTask_query_agrees_with_IsFinal_for_every_step()
        {
            var isUnfinished = FederatedTaskSteps.IsUnfinishedTask.Compile();
            foreach (var step in Enum.GetValues<FederatedTaskStep>())
            {
                Assert.Equal(!FederatedTaskSteps.IsFinal(step), isUnfinished(new FederatedTask { Step = step }));
            }
        }

        [Fact]
        public void MoveTo_final_sets_CompletedAt_and_appends_history()
        {
            var task = new FederatedTask();
            task.MoveTo(FederatedTaskStep.Queued, T0);
            Assert.Null(task.CompletedAt);

            task.MoveTo(FederatedTaskStep.Released, T0.AddMinutes(5));

            Assert.Equal(T0.AddMinutes(5), task.CompletedAt);
            Assert.Equal(new[] { FederatedTaskStep.Queued, FederatedTaskStep.Released }, task.StepChanges.Select(c => c.Step).ToArray());
        }

        private static FederatedTask NewTask()
        {
            return new FederatedTask { TaskId = Guid.NewGuid(), SubmittedAt = T0, TesTaskJson = "{}" };
        }

        [Fact]
        public void ToTesTask_waiting_shows_step_and_times()
        {
            var task = NewTask();
            task.MoveTo(FederatedTaskStep.Queued, T0);
            task.MoveTo(FederatedTaskStep.Running, T0.AddMinutes(1));
            task.MoveTo(FederatedTaskStep.WaitingForOutputApproval, T0.AddMinutes(2));

            var tes = task.ToTesTask();
            var log = Assert.Single(tes.Logs!);

            Assert.Equal(TesState.PAUSEDEnum, tes.State);
            Assert.Equal("waiting_for_output_approval", log.Metadata["step"]);
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
            task.MoveTo(FederatedTaskStep.Queued, T0);
            task.Reject(FederatedTaskStep.RejectedByInputChecks, T0.AddMinutes(1), "dataset_not_found", "dataset 'x' not found");

            var tes = task.ToTesTask();
            var m = tes.Logs![0].Metadata;

            Assert.Equal(TesState.CANCELEDEnum, tes.State);
            Assert.Equal("rejected_by_input_checks", m["step"]);
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
            task.MoveTo(FederatedTaskStep.Queued, T0);
            task.MoveTo(FederatedTaskStep.Released, T0.AddMinutes(3));

            var log = task.ToTesTask().Logs![0];

            Assert.Equal("b", log.Metadata["result_bucket"]);
            Assert.Equal("k", log.Metadata["result_key"]);
            Assert.Equal(new string('a', 64), log.Metadata["result_sha256"]);
            Assert.True(log.Metadata.ContainsKey("released_at"));
            Assert.Equal(new DateTimeOffset(T0.AddMinutes(3)), log.EndTime);
        }
    }
}
