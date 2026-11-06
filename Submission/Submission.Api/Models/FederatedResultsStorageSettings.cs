using FiveSafesTes.Core.Models.ViewModels;

namespace Submission.Api.Models
{
    /// <summary>
    /// Connection settings for the orchestrator-side RustFS that holds federated task result files.
    /// Separate from <c>MinioSettings</c>, which is the researcher-facing project store.
    /// </summary>
    public class FederatedResultsStorageSettings
    {
        public const string SectionName = "FederatedResultsStorageSettings";

        public string Url { get; set; } = "";
        public string AccessKey { get; set; } = "";
        public string SecretKey { get; set; } = "";

        // TODO: store results in the project's output bucket once study_id → project mapping exists
        public string Bucket { get; set; } = "federated-results";

        public string AWSRegion { get; set; } = "us-east-1";

        public MinioSettings ToMinioSettings() => new()
        {
            Url = Url,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            BucketName = Bucket,
            AWSRegion = AWSRegion
        };

        /// <summary>Names of the required settings that are empty, as configuration keys.</summary>
        public IReadOnlyList<string> MissingSettings()
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(Url)) missing.Add($"{SectionName}:{nameof(Url)}");
            if (string.IsNullOrWhiteSpace(AccessKey)) missing.Add($"{SectionName}:{nameof(AccessKey)}");
            if (string.IsNullOrWhiteSpace(SecretKey)) missing.Add($"{SectionName}:{nameof(SecretKey)}");
            if (string.IsNullOrWhiteSpace(Bucket)) missing.Add($"{SectionName}:{nameof(Bucket)}");
            return missing;
        }

        public static FederatedResultsStorageSettings FromConfiguration(IConfiguration configuration)
        {
            var settings = new FederatedResultsStorageSettings();
            configuration.GetSection(SectionName).Bind(settings);
            return settings;
        }
    }
}
