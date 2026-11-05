using System.Text.RegularExpressions;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    public static class FederatedResultLimits
    {
        public const int MaxResultBytes = 65536;
        public const int MaxReportRequestBytes = 73728;
        public const int MaxErrorMessageLength = 512;
        public const int MaxJsonDepth = 16;

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
