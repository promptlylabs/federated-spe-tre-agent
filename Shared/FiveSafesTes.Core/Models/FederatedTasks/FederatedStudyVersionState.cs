namespace FiveSafesTes.Core.Models.FederatedTasks
{
    public enum FederatedStudyVersionState
    {
        Open,
        Canceling,
        Ended
    }

    public static class FederatedStudyVersionStates
    {
        public static string ToWire(this FederatedStudyVersionState state) => state switch
        {
            FederatedStudyVersionState.Open => "open",
            FederatedStudyVersionState.Canceling => "canceling",
            FederatedStudyVersionState.Ended => "ended",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }
}
