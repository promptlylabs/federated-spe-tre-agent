using System.Text.RegularExpressions;

namespace Submission.Api.Services
{
    public static class FederatedResultKeys
    {
        private static readonly Regex Sha256Pattern = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);

        /// <summary>Builds the orchestrator object key <c>&lt;treId&gt;/&lt;taskId&gt;/&lt;sha256&gt;.json</c>.</summary>
        /// <summary>The folder that holds every result file of the task.</summary>
        public static string PrefixFor(int treId, Guid taskId) => $"{treId}/{taskId:D}/";

        /// <summary>
        /// Builds the key of a study version's pooled result, <c>studies/&lt;versionId&gt;/&lt;sha256&gt;.json</c>: apart from
        /// the site results (<c>&lt;treId&gt;/…</c>), so a pooled-result route can never serve a hospital's own result.
        /// </summary>
        public static string ForPooled(Guid versionId, string sha256)
        {
            CheckSha(sha256);
            return $"studies/{versionId:D}/{sha256}.json";
        }

        private static void CheckSha(string sha256)
        {
            if (sha256 is null || !Sha256Pattern.IsMatch(sha256))
            {
                throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(sha256));
            }
        }

        public static string For(int treId, Guid taskId, string sha256)
        {
            if (sha256 is null || !Sha256Pattern.IsMatch(sha256))
            {
                throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(sha256));
            }

            return $"{PrefixFor(treId, taskId)}{sha256}.json";
        }
    }
}
