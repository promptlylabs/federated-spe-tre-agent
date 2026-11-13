using System.Linq.Expressions;
using FiveSafesTes.Core.Models.Tes;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Where a federated task is, from intake to release.</summary>
    public enum FederatedTaskStep
    {
        Queued,
        Running,
        WaitingForOutputApproval,
        Released,
        RejectedByInputChecks,
        RejectedAtOutputApproval,
        Failed
    }

    /// <summary>What a requested step change would do to a task.</summary>
    public enum FederatedTaskTransition
    {
        Apply,
        NoChange,
        Backwards,
        AlreadyFinal
    }

    public static class FederatedTaskSteps
    {
        /// <summary>
        /// The task is in a step that is not final. For EF queries; a test keeps it in agreement with <see cref="IsFinal"/>.
        /// </summary>
        public static readonly Expression<Func<FederatedTask, bool>> IsUnfinishedTask = x =>
            x.Step == FederatedTaskStep.Queued
            || x.Step == FederatedTaskStep.Running
            || x.Step == FederatedTaskStep.WaitingForOutputApproval;

        public static string ToWire(FederatedTaskStep step) => step switch
        {
            FederatedTaskStep.Queued => "queued",
            FederatedTaskStep.Running => "running",
            FederatedTaskStep.WaitingForOutputApproval => "waiting_for_output_approval",
            FederatedTaskStep.Released => "released",
            FederatedTaskStep.RejectedByInputChecks => "rejected_by_input_checks",
            FederatedTaskStep.RejectedAtOutputApproval => "rejected_at_output_approval",
            FederatedTaskStep.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
        };

        public static bool TryParse(string? value, out FederatedTaskStep step)
        {
            foreach (var candidate in Enum.GetValues<FederatedTaskStep>())
            {
                if (value == ToWire(candidate))
                {
                    step = candidate;
                    return true;
                }
            }

            step = default;
            return false;
        }

        public static bool IsFinal(FederatedTaskStep step) => step switch
        {
            FederatedTaskStep.Queued or FederatedTaskStep.Running or FederatedTaskStep.WaitingForOutputApproval => false,
            FederatedTaskStep.Released or FederatedTaskStep.RejectedByInputChecks
                or FederatedTaskStep.RejectedAtOutputApproval or FederatedTaskStep.Failed => true,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
        };

        public static bool IsRejection(FederatedTaskStep step) =>
            step is FederatedTaskStep.RejectedByInputChecks or FederatedTaskStep.RejectedAtOutputApproval;

        public static FederatedTaskTransition Check(FederatedTaskStep current, FederatedTaskStep next)
        {
            if (IsFinal(current))
            {
                return current == next ? FederatedTaskTransition.NoChange : FederatedTaskTransition.AlreadyFinal;
            }

            var c = Rank(current);
            var n = Rank(next);
            return n > c ? FederatedTaskTransition.Apply : n == c ? FederatedTaskTransition.NoChange : FederatedTaskTransition.Backwards;
        }

        /// <summary>The TES state of every step except <see cref="FederatedTaskStep.Failed"/>, whose state the reporter chooses.</summary>
        internal static TesState ToTesState(FederatedTaskStep step) => step switch
        {
            FederatedTaskStep.Queued => TesState.QUEUEDEnum,
            FederatedTaskStep.Running => TesState.RUNNINGEnum,
            FederatedTaskStep.WaitingForOutputApproval => TesState.PAUSEDEnum,
            FederatedTaskStep.Released => TesState.COMPLETEEnum,
            FederatedTaskStep.RejectedByInputChecks or FederatedTaskStep.RejectedAtOutputApproval => TesState.CANCELEDEnum,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "a failed step has no fixed TES state")
        };

        private static int Rank(FederatedTaskStep step) => step switch
        {
            FederatedTaskStep.Queued => 0,
            FederatedTaskStep.Running => 1,
            FederatedTaskStep.WaitingForOutputApproval => 2,
            FederatedTaskStep.Released or FederatedTaskStep.RejectedByInputChecks
                or FederatedTaskStep.RejectedAtOutputApproval or FederatedTaskStep.Failed => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
        };
    }
}
