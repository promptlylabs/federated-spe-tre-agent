using System.Text;
using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// What a scan found. <see cref="CodeScanKind.RulesUnavailable"/>: the hospital's rule file is missing, unreadable or
    /// has no version line (a broken config, not the researcher's fault). <see cref="CodeScanKind.ScannerFailed"/>: the
    /// scanner itself failed or ran too long.
    /// </summary>
    public enum CodeScanKind { Clean, Findings, RulesUnavailable, ScannerFailed }

    /// <summary>One thing a rule found: the rule's id, the line and the rule's message. Never the code's text.</summary>
    public sealed record CodeScanFinding(string RuleId, int Line, string Message);

    /// <summary>
    /// A scan's outcome, with the version and SHA-256 of the rule file it used (null when the rules were unavailable).
    /// <see cref="Detail"/> is for logs only: the scanner's stderr, or why the rules are unavailable.
    /// </summary>
    public sealed record CodeScanResult(
        CodeScanKind Kind,
        IReadOnlyList<CodeScanFinding> Findings,
        string? RulesVersion,
        string? RulesSha256,
        string? Detail);

    /// <summary>Checks researcher code against the hospital's rules before any data is touched.</summary>
    public interface ICodeScanner
    {
        Task<CodeScanResult> ScanAsync(string code, CancellationToken ct);
    }

    public static class CodeScanMessages
    {
        public const string ParseErrorRuleId = "parse-error";
        public const string ParseErrorMessage = "the code could not be parsed";

        /// <summary>
        /// The refusal message: each finding as "&lt;rule-id&gt; line &lt;n&gt;: &lt;message&gt;", sorted by line, joined with "; ",
        /// at most <see cref="FederatedResultLimits.MaxErrorMessageLength"/> characters. When findings are left out it
        /// ends with "; … and &lt;k&gt; more".
        /// </summary>
        public static string Format(IReadOnlyList<CodeScanFinding> findings)
        {
            var parts = findings
                .OrderBy(f => f.Line)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .Select(f => $"{f.RuleId} line {f.Line}: {OneLine(f.Message)}")
                .ToList();
            const int max = FederatedResultLimits.MaxErrorMessageLength;

            var joined = string.Join("; ", parts);
            if (joined.Length <= max)
            {
                return joined;
            }

            var builder = new StringBuilder();
            for (var kept = 0; kept < parts.Count; kept++)
            {
                var next = (kept == 0 ? "" : "; ") + parts[kept];
                var suffix = Suffix(parts.Count - kept - 1);
                if (builder.Length + next.Length + suffix.Length > max)
                {
                    if (kept == 0)
                    {
                        // Even the first finding doesn't fit: cut it.
                        suffix = Suffix(parts.Count - 1);
                        return parts[0][..(max - suffix.Length)] + suffix;
                    }

                    return builder + Suffix(parts.Count - kept);
                }

                builder.Append(next);
            }

            return builder.ToString();
        }

        private static string Suffix(int leftOut) => leftOut == 0 ? "" : $"; … and {leftOut} more";

        /// <summary>Control characters (a rule message may span lines) become single spaces.</summary>
        private static string OneLine(string message) =>
            string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new string(word.Where(c => !char.IsControl(c)).ToArray()))
                .Where(word => word.Length > 0));
    }
}
