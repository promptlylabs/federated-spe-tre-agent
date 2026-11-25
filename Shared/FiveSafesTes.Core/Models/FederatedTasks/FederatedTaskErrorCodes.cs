namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// Error codes reported back to the orchestrator when a federated task fails.
    /// </summary>
    public static class FederatedTaskErrorCodes
    {
        public const string InvalidTask = "invalid_task";
        public const string InvalidDatasetName = "invalid_dataset_name";
        public const string DatasetNotFound = "dataset_not_found";
        public const string AmbiguousDataset = "ambiguous_dataset";
        public const string InvalidDataset = "invalid_dataset";
        public const string SchemaNotFound = "schema_not_found";
        public const string ImageNotApproved = "image_not_approved";
        public const string UnsafeCode = "unsafe_code";
        public const string InternalError = "internal_error";
        public const string TreRenamed = "tre_renamed";
        public const string InvalidResult = "invalid_result";
        public const string ResultTooLarge = "result_too_large";
        public const string ResultNotFound = "result_not_found";
        public const string StorageUnavailable = "storage_unavailable";
        public const string RejectedByReviewer = "rejected_by_reviewer";
        public const string ResultChangedAfterReview = "result_changed_after_review";
        public const string Timeout = "timeout";
        public const string OutOfMemory = "out_of_memory";
        public const string RunFailed = "run_failed";

        /// <summary>The researcher's code could not be imported, lacks the function, or raised (a runner code).</summary>
        public const string CodeError = "code_error";

        /// <summary>A study version waited longer than AggregationTimeoutMinutes for the aggregator.</summary>
        public const string AggregationTimeout = "aggregation_timeout";

        /// <summary>Codes that mean the task was rejected by the input checks; every other code is a failure.</summary>
        public static readonly IReadOnlySet<string> InputCheckCodes = new HashSet<string>
        {
            InvalidTask, InvalidDatasetName, DatasetNotFound, AmbiguousDataset, SchemaNotFound, ImageNotApproved, UnsafeCode
        };

        // Returned only in API error bodies, never in task metadata.
        public const string TaskNotFound = "task_not_found";
        public const string TaskAlreadyReported = "task_already_reported";
        public const string InvalidTransition = "invalid_transition";
        // Other reports for the same task kept winning the race; the report was not applied and can be sent again.
        public const string TaskBusy = "task_busy";

        /// <summary>The orchestrator cancelled the task; it accepts no result or progress until the hospital confirms.</summary>
        public const string TaskCanceling = "task_canceling";

        /// <summary>A cancel was asked for a task that is already in a final phase.</summary>
        public const string TaskAlreadyFinished = "task_already_finished";

        /// <summary>A new version of a study was submitted while its latest version is still open.</summary>
        public const string StudyVersionOpen = "study_version_open";

        /// <summary>No version of the study exists.</summary>
        public const string StudyNotFound = "study_not_found";
    }
}
