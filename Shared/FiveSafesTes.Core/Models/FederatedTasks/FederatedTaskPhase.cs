using System.Linq.Expressions;
using FiveSafesTes.Core.Models.Tes;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Where a federated task is, from intake to release.</summary>
    public enum FederatedTaskPhase
    {
        Queued,
        Running,
        WaitingForOutputApproval,
        Released,
        RejectedByInputChecks,
        RejectedAtOutputApproval,
        Failed,
        Canceling,
        Canceled
    }

    /// <summary>What a requested phase change would do to a task.</summary>
    public enum FederatedTaskTransition
    {
        Apply,
        NoChange,
        Backwards,
        AlreadyFinal
    }

    public static class FederatedTaskPhases
    {
        /// <summary>
        /// The task is in a phase that is not final. For EF queries; a test keeps it in agreement with <see cref="IsFinal"/>.
        /// </summary>
        public static readonly Expression<Func<FederatedTask, bool>> IsUnfinishedTask = x =>
            x.Phase == FederatedTaskPhase.Queued
            || x.Phase == FederatedTaskPhase.Running
            || x.Phase == FederatedTaskPhase.WaitingForOutputApproval
            || x.Phase == FederatedTaskPhase.Canceling;

        public static string ToWire(FederatedTaskPhase phase) => phase switch
        {
            FederatedTaskPhase.Queued => "queued",
            FederatedTaskPhase.Running => "running",
            FederatedTaskPhase.WaitingForOutputApproval => "waiting_for_output_approval",
            FederatedTaskPhase.Released => "released",
            FederatedTaskPhase.RejectedByInputChecks => "rejected_by_input_checks",
            FederatedTaskPhase.RejectedAtOutputApproval => "rejected_at_output_approval",
            FederatedTaskPhase.Failed => "failed",
            FederatedTaskPhase.Canceling => "canceling",
            FederatedTaskPhase.Canceled => "canceled",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };

        public static bool TryParse(string? value, out FederatedTaskPhase phase)
        {
            foreach (var candidate in Enum.GetValues<FederatedTaskPhase>())
            {
                if (value == ToWire(candidate))
                {
                    phase = candidate;
                    return true;
                }
            }

            phase = default;
            return false;
        }

        /// <summary>The phase a task carries in its first log's metadata, as GetTask and GetPendingTasksForTre return it.</summary>
        public static FederatedTaskPhase? PhaseOf(TesTask task) =>
            TryParse(task.Logs?.FirstOrDefault()?.Metadata?.GetValueOrDefault("phase"), out var phase) ? phase : null;

        public static bool IsFinal(FederatedTaskPhase phase) => phase switch
        {
            FederatedTaskPhase.Queued or FederatedTaskPhase.Running or FederatedTaskPhase.WaitingForOutputApproval
                or FederatedTaskPhase.Canceling => false,
            FederatedTaskPhase.Released or FederatedTaskPhase.RejectedByInputChecks
                or FederatedTaskPhase.RejectedAtOutputApproval or FederatedTaskPhase.Failed or FederatedTaskPhase.Canceled => true,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };

        public static bool IsRejection(FederatedTaskPhase phase) =>
            phase is FederatedTaskPhase.RejectedByInputChecks or FederatedTaskPhase.RejectedAtOutputApproval;

        public static FederatedTaskTransition Check(FederatedTaskPhase current, FederatedTaskPhase next)
        {
            if (IsFinal(current))
            {
                return current == next ? FederatedTaskTransition.NoChange : FederatedTaskTransition.AlreadyFinal;
            }

            var c = Rank(current);
            var n = Rank(next);
            return n > c ? FederatedTaskTransition.Apply : n == c ? FederatedTaskTransition.NoChange : FederatedTaskTransition.Backwards;
        }

        /// <summary>The TES state of every phase except <see cref="FederatedTaskPhase.Failed"/>, whose state the reporter chooses.</summary>
        internal static TesState ToTesState(FederatedTaskPhase phase) => phase switch
        {
            FederatedTaskPhase.Queued => TesState.QUEUEDEnum,
            FederatedTaskPhase.Running => TesState.RUNNINGEnum,
            FederatedTaskPhase.WaitingForOutputApproval => TesState.RUNNINGEnum,
            FederatedTaskPhase.Released => TesState.COMPLETEEnum,
            FederatedTaskPhase.RejectedByInputChecks or FederatedTaskPhase.RejectedAtOutputApproval => TesState.CANCELEDEnum,
            FederatedTaskPhase.Canceling => TesState.CANCELINGEnum,
            FederatedTaskPhase.Canceled => TesState.CANCELEDEnum,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "a failed phase has no fixed TES state")
        };

        private static int Rank(FederatedTaskPhase phase) => phase switch
        {
            FederatedTaskPhase.Queued => 0,
            FederatedTaskPhase.Running => 1,
            FederatedTaskPhase.WaitingForOutputApproval => 2,
            FederatedTaskPhase.Canceling => 3,
            FederatedTaskPhase.Released or FederatedTaskPhase.RejectedByInputChecks
                or FederatedTaskPhase.RejectedAtOutputApproval or FederatedTaskPhase.Failed or FederatedTaskPhase.Canceled => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };
    }
}
