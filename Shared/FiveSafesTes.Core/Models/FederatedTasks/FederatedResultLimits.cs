using System.Text.RegularExpressions;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    public static class FederatedResultLimits
    {
        // TODO: revisit when a result kind other than scalar is added. A scalar document cannot reach this limit (tags
        // are capped at Enqueue), so the Agent does not check it. A larger kind needs an Agent-side check: a file over
        // MaxReportRequestBytes is refused as invalid_task, which the Agent retries on every poll.
        public const int MaxResultBytes = 65536;
        public const int MaxReportRequestBytes = 73728;
        public const int MaxErrorMessageLength = 512;
        public const int MaxJsonDepth = 16;

        /// <summary>
        /// Longest string field in a result document. Enqueue applies it to the tags copied into the document
        /// (tres, study_id), so a task that could never be reported is never queued.
        /// </summary>
        public const int MaxStringLength = 1024;

        public static readonly Regex ErrorCodePattern = new(@"^[a-z_]{1,64}\z", RegexOptions.Compiled);

        /// <summary>Null or empty is valid; otherwise at most 512 chars and no control characters.</summary>
        public static bool IsValidErrorMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return true;
            }

            return message.Length <= MaxErrorMessageLength && !message.Any(char.IsControl);
        }

        public static string TruncateErrorMessage(string message) =>
            message.Length <= MaxErrorMessageLength ? message : message[..MaxErrorMessageLength];
    }
}
