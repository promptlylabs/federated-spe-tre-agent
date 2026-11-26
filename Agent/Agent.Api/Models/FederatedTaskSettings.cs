using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.Extensions.Configuration;

namespace Agent.Api.Models
{
    /// <summary>
    /// Settings for federated task execution on the TRE Agent.
    /// </summary>
    public class FederatedTaskSettings
    {
        /// <summary>
        /// Dedicated TRE bucket holding the dataset CSVs that federated tasks read. Kept separate
        /// from project submission/output buckets so its access can be locked down on its own,
        /// and so expired-project bucket cleanup never touches it. Created at startup if missing.
        /// </summary>
        public string DatasetBucket { get; set; } = "federated-datasets";

        /// <summary>
        /// Key prefix under which each dataset lives, as {DatasetPrefix}{datasetName}/*.csv.
        /// </summary>
        public string DatasetPrefix { get; set; } = "dataset/";

        /// <summary>
        /// File extensions (without the dot) that the upload policy allows to be uploaded. This does not
        /// change what the dataset reader parses; it reads only .csv files.
        /// </summary>
        public string[] AllowedExtensions { get; set; } = ["csv"];

        /// <summary>
        /// Name of the RustFS canned policy, created at startup, that grants dataset upload.
        /// </summary>
        public string UploadPolicyName { get; set; } = "dataset-upload";

        /// <summary>
        /// Dedicated TRE bucket holding one result file per federated task. Separate from the project
        /// output buckets so its access can be locked down on its own. Created at startup if missing.
        /// </summary>
        public string OutputBucket { get; set; } = "federated-outputs";

        /// <summary>
        /// Name of the RustFS canned policy, created at startup, that grants read-only access to the output bucket.
        /// </summary>
        public string OutputReadPolicyName { get; set; } = "federated-outputs-read";

        /// <summary>
        /// How long, in minutes, the Agent keeps retrying a task whose dataset bucket cannot be listed or read. The time
        /// counts from when the task was queued, or from when the Agent started if that is later, so a task held while
        /// ApprovedImages was missing, or queued while the Agent was down, gets the whole window. Each Agent restart gives
        /// pending tasks the whole window again. Until then a failure is treated as an outage and the task is tried again
        /// at the next poll; after it, the task fails with internal_error so a bucket that is wrongly configured does not
        /// leave it unfinished for good. A run that hits a Docker fault that may pass by itself (the daemon restarting, a
        /// server error) gets the same number of minutes, counted from its first fault (<see cref="DockerFaultTimes"/>).
        /// 0 or less gives up at the first failure.
        /// </summary>
        public int DatasetUnreachableGiveUpMinutes { get; set; } = 60;

        /// <summary>
        /// How long, in minutes, the Agent keeps trying to register a saved result with the Egress app when the
        /// registration fails (the Egress app is down, unreachable or refuses it). The time counts from when the
        /// result was first sent for review, or from when the Agent started if that is later. Until then the task
        /// keeps its phase and is tried again at the next poll; after it, the task fails with internal_error, so a
        /// broken Egress connection does not leave finished tasks running for good. The default, one day, lets an
        /// Egress outage pass without failing finished work. 0 or less gives up at the first failure.
        /// </summary>
        public int EgressUnreachableGiveUpMinutes { get; set; } = 1440;

        /// <summary>
        /// Executor images this TRE has approved. A task whose image is not listed is rejected by the input checks with
        /// image_not_approved. There is no default: approving an image is the site's decision. When the list is missing
        /// or empty, a task that fails the profile is still rejected, but no other new task is checked, run or
        /// reported: a forgotten setting never rejects anything. Entries are trimmed and blank ones dropped, however the
        /// list is set; matching is exact and case-sensitive.
        /// </summary>
        public string[] ApprovedImages
        {
            get => _approvedImages;
            set => _approvedImages = (value ?? [])
                .Where(image => !string.IsNullOrWhiteSpace(image))
                .Select(image => image.Trim())
                .ToArray();
        }

        private string[] _approvedImages = [];

        /// <summary>
        /// A message for the site admin when the list is set but holds no image the Agent runs: an entry whose image name
        /// (the last '/' segment, before the ':' tag) is python-runner or federated-ops. Every task is then rejected with
        /// image_not_approved. That can be a deliberate refusal or a typo. The Agent cannot tell them apart, so it logs
        /// this as an error at startup but still applies the list. Null otherwise, and when the list is empty.
        /// </summary>
        public string? ApprovedImagesProblem() =>
            ApprovedImages.Length == 0 || ApprovedImages.Any(image => KnownImageNames.Contains(ImageName(image)))
                ? null
                : $"FederatedTaskSettings:ApprovedImages lists neither '{RunnerImageName}' nor '{OpsImageName}', the images federated tasks use, " +
                  "so every federated task will be rejected with image_not_approved. If that is not intended, check the list for a typo.";

        private const string RunnerImageName = "python-runner";
        private const string OpsImageName = "federated-ops";
        private static readonly string[] KnownImageNames = [RunnerImageName, OpsImageName];

        private static string ImageName(string image)
        {
            var lastSegment = image[(image.LastIndexOf('/') + 1)..];
            var tag = lastSegment.IndexOf(':');
            return tag < 0 ? lastSegment : lastSegment[..tag];
        }

        /// <summary>
        /// The longest, in minutes, that researcher code may run in its container. After it the container is killed and the
        /// task fails with timeout.
        /// </summary>
        public int MaxRunMinutes { get; set; } = 30;

        /// <summary>CPU cores a run gets when the task does not ask for a number.</summary>
        public double DefaultCpuCores { get; set; } = 1;

        /// <summary>The most CPU cores a task may ask for.</summary>
        public double MaxCpuCores { get; set; } = 4;

        /// <summary>RAM, in GB, a run gets when the task does not ask for an amount.</summary>
        public double DefaultRamGb { get; set; } = 8;

        /// <summary>The most RAM, in GB, a task may ask for.</summary>
        public double MaxRamGb { get; set; } = 16;

        /// <summary>Scratch disk, in GB, a run gets when the task does not ask for an amount.</summary>
        public double DefaultDiskGb { get; set; } = 1;

        /// <summary>The most scratch disk, in GB, a task may ask for.</summary>
        public double MaxDiskGb { get; set; } = 4;

        /// <summary>How many researcher-code containers may run at the same time on this Agent.</summary>
        public int MaxConcurrentRuns { get; set; } = 2;

        /// <summary>
        /// A message for the site admin when a code-task setting stops code tasks from working: MaxConcurrentRuns of 0 or
        /// less (no code task ever starts) or MaxRunMinutes of 0 or less (every run times out at once). Logged as an
        /// error at startup; the settings are still used. Null when both are above 0.
        /// </summary>
        public string? RunSettingsProblem()
        {
            var problems = new List<string>();
            if (MaxConcurrentRuns <= 0)
            {
                problems.Add($"FederatedTaskSettings:MaxConcurrentRuns is {MaxConcurrentRuns}, so no code task will ever start; " +
                             "code tasks keep their phase");
            }

            if (MaxRunMinutes <= 0)
            {
                problems.Add($"FederatedTaskSettings:MaxRunMinutes is {MaxRunMinutes}, so every code run will fail with timeout " +
                             "at the first check");
            }

            return problems.Count == 0 ? null : string.Join(". ", problems) + ". Set the value above 0.";
        }

        /// <summary>Docker endpoint the Agent uses to start the containers that run researcher code.</summary>
        public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

        public static FederatedTaskSettings FromConfiguration(IConfiguration configuration)
        {
            var section = configuration.GetSection(nameof(FederatedTaskSettings));
            var settings = new FederatedTaskSettings();
            section.Bind(settings);

            // Binding appends configured array items to the default, so a configured list must replace it here.
            if (section.GetSection(nameof(AllowedExtensions)).Get<string[]>() is { Length: > 0 } extensions)
            {
                settings.AllowedExtensions = extensions;
            }

            // A single value ("img" or "img1,img2") is split, not lost. It replaces any indexed entries
            // ("ApprovedImages:0"), as an environment variable is expected to override appsettings.
            if (section[nameof(ApprovedImages)] is { } single && !string.IsNullOrWhiteSpace(single))
            {
                settings.ApprovedImages = single.Split(',');
            }

            return settings;
        }
    }
}
