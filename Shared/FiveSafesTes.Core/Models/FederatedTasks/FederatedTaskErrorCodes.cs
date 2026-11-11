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
        public const string UnknownOperation = "unknown_operation";
        public const string ImageNotApproved = "image_not_approved";
        public const string InvalidFilter = "invalid_filter";
        public const string ColumnNotFound = "column_not_found";
        public const string NonNumericValue = "non_numeric_value";
        public const string InternalError = "internal_error";
        public const string TreRenamed = "tre_renamed";
        public const string InvalidResult = "invalid_result";
        public const string ResultTooLarge = "result_too_large";
        public const string ResultNotFound = "result_not_found";
        public const string StorageUnavailable = "storage_unavailable";
        public const string RejectedByReviewer = "rejected_by_reviewer";
        public const string ResultChangedAfterReview = "result_changed_after_review";

        /// <summary>Codes that mean the task was rejected by the input checks; every other code is a failure.</summary>
        public static readonly IReadOnlySet<string> InputCheckCodes = new HashSet<string>
        {
            InvalidTask, UnknownOperation, InvalidFilter, InvalidDatasetName, DatasetNotFound, AmbiguousDataset, ImageNotApproved
        };

        // Returned only in API error bodies, never in task metadata.
        public const string TaskNotFound = "task_not_found";
        public const string TaskAlreadyReported = "task_already_reported";
        public const string InvalidTransition = "invalid_transition";
        // Other reports for the same task kept winning the race; the report was not applied and can be sent again.
        public const string TaskBusy = "task_busy";
    }
}
