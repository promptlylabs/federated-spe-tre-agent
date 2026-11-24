namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Fixed error sentences for a researcher-code run. They hold no data from the run, so they are safe to return to
    /// the orchestrator.
    /// </summary>
    public static class RunErrorMessages
    {
        public const string InvalidResultFile = "the run produced a result file the TRE cannot accept";
        public const string RunFailed = "the run ended without a result";
        public const string OutOfMemory = "the code ran out of memory";

        public static string Timeout(int minutes) => $"the code ran longer than the TRE's limit of {minutes} minutes";

        /// <summary>The sentence for an error code the runner reports, or null when the code is not one the runner uses.</summary>
        public static string? ForRunnerCode(string code) => code switch
        {
            "dataset_not_found" => "the runner found no dataset file",
            "ambiguous_dataset" => "the runner found more than one dataset file",
            "dataset_too_large" => "the dataset is larger than the runner's 256 MiB limit",
            "invalid_dataset" => "the runner could not read the dataset as CSV",
            "schema_not_found" => "the runner found no schema.json for the dataset",
            "invalid_schema" => "the dataset's schema.json is not valid",
            "schema_mismatch" => "the dataset does not match its schema.json",
            "code_error" => "the code could not be imported, has no site_analysis, or site_analysis raised an exception",
            "invalid_result" => "site_analysis did not return a valid result",
            "result_too_large" => "the result returned by site_analysis is larger than 61,440 bytes",
            _ => null,
        };
    }
}
