namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// What the orchestrator allows a code task to ask for: the runner images it may use and the largest resources.
    /// Passed to <see cref="FederatedTesProfile.Parse"/> at intake only; the Agent parses without it.
    /// </summary>
    public sealed record FederatedIntakeLimits(
        IReadOnlyList<string> ApprovedRunnerImages,
        long MaxCpuCores,
        double MaxRamGb,
        double MaxDiskGb);
}
