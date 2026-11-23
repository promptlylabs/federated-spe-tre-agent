using FiveSafesTes.Core.Models.FederatedTasks;

namespace Submission.Api.Models
{
    /// <summary>
    /// What intake allows a code task to ask for: the runner images it may use and the largest resources.
    /// The resource defaults are placeholders until the hospitals confirm their capacity.
    /// </summary>
    public class FederatedTaskSettings
    {
        public const string SectionName = "FederatedTaskSettings";

        public static readonly IReadOnlyList<string> DefaultRunnerImages = ["5s-tes/python-runner:1"];

        /// <summary>
        /// Executor images a code task may use. Exact, case-sensitive match. Empty rejects every code task. The default
        /// lives here, not in appsettings.json: configuration lists merge by index, so a shipped list could never be
        /// shortened or emptied by an override file or environment variables.
        /// </summary>
        public string[] ApprovedRunnerImages { get; set; } = [.. DefaultRunnerImages];

        public long MaxCpuCores { get; set; } = 4;
        public double MaxRamGb { get; set; } = 16;
        public double MaxDiskGb { get; set; } = 20;

        public FederatedIntakeLimits ToIntakeLimits() => new(
            ApprovedRunnerImages.Select(i => i.Trim()).Where(i => i.Length > 0).ToArray(),
            MaxCpuCores, MaxRamGb, MaxDiskGb);

        public static FederatedTaskSettings FromConfiguration(IConfiguration configuration)
        {
            var section = configuration.GetSection(SectionName);
            // The binder adds configured array entries to the ones already there, so start the list empty: configured
            // images replace the default instead of adding to it.
            var settings = new FederatedTaskSettings { ApprovedRunnerImages = [] };
            section.Bind(settings);
            if (!section.GetSection(nameof(ApprovedRunnerImages)).Exists())
                settings.ApprovedRunnerImages = [.. DefaultRunnerImages];
            return settings;
        }
    }
}
