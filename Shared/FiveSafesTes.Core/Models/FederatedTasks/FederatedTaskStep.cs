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

    public enum TransitionResult
    {
        Apply,
        NoChange,
        Backwards,
        AlreadyFinal
    }

    public static class FederatedTaskSteps
    {
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

        public static bool IsFinal(FederatedTaskStep step) => Rank(step) == 3;

        public static TransitionResult Check(FederatedTaskStep current, FederatedTaskStep next)
        {
            if (IsFinal(current))
            {
                return current == next ? TransitionResult.NoChange : TransitionResult.AlreadyFinal;
            }

            var c = Rank(current);
            var n = Rank(next);
            return n > c ? TransitionResult.Apply : n == c ? TransitionResult.NoChange : TransitionResult.Backwards;
        }

        internal static TesState ToTesState(FederatedTaskStep step, TesState? failedState) => step switch
        {
            FederatedTaskStep.Queued => TesState.QUEUEDEnum,
            FederatedTaskStep.Running => TesState.RUNNINGEnum,
            FederatedTaskStep.WaitingForOutputApproval => TesState.PAUSEDEnum,
            FederatedTaskStep.Released => TesState.COMPLETEEnum,
            FederatedTaskStep.RejectedByInputChecks or FederatedTaskStep.RejectedAtOutputApproval => TesState.CANCELEDEnum,
            FederatedTaskStep.Failed => failedState is TesState.EXECUTORERROREnum or TesState.SYSTEMERROREnum
                ? failedState.Value
                : throw new ArgumentException("failedState must be EXECUTOR_ERROR or SYSTEM_ERROR for a failed step", nameof(failedState)),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
        };

        private static int Rank(FederatedTaskStep step) => step switch
        {
            FederatedTaskStep.Queued => 0,
            FederatedTaskStep.Running => 1,
            FederatedTaskStep.WaitingForOutputApproval => 2,
            _ => 3
        };
    }
}
