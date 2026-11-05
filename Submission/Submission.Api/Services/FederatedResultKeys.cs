using System.Text.RegularExpressions;

namespace Submission.Api.Services
{
    public static class FederatedResultKeys
    {
        private static readonly Regex Sha256Pattern = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);

        /// <summary>Builds the orchestrator object key <c>&lt;treId&gt;/&lt;taskId&gt;/&lt;sha256&gt;.json</c>.</summary>
        public static string For(int treId, Guid taskId, string sha256)
        {
            if (sha256 is null || !Sha256Pattern.IsMatch(sha256))
            {
                throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(sha256));
            }

            return $"{treId}/{taskId:D}/{sha256}.json";
        }
    }
}
