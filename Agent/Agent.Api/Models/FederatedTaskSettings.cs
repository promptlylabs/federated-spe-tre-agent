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
        /// How long, in minutes after a task was queued, the Agent keeps retrying a task whose dataset bucket cannot
        /// be listed or read. Until then a failure is treated as an outage and the task is tried again at the next
        /// poll; after it, the task fails with internal_error so a bucket that is wrongly configured does not leave it
        /// unfinished for good.
        /// 0 or less gives up at the first failure.
        /// </summary>
        public int DatasetUnreachableGiveUpMinutes { get; set; } = 60;

        /// <summary>
        /// Executor images this TRE has approved. A task whose image is not listed is rejected by the input checks with
        /// image_not_approved. There is no default: approving an image is the site's decision. When the list is missing
        /// or empty the Agent runs no new task and reports none, so a forgotten setting never rejects anything.
        /// Entries are trimmed and blank ones dropped; matching is exact and case-sensitive.
        /// </summary>
        public string[] ApprovedImages { get; set; } = [];

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

            settings.ApprovedImages = settings.ApprovedImages
                .Where(image => !string.IsNullOrWhiteSpace(image))
                .Select(image => image.Trim())
                .ToArray();

            return settings;
        }
    }
}
