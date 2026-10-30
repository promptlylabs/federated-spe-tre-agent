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
        /// Binds the settings from their configuration section.
        /// </summary>
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

            return settings;
        }
    }
}
