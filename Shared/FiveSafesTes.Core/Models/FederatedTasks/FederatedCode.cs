using System.Text.RegularExpressions;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>Reading the researcher's analysis.py for people (the Egress review page); never for running it.</summary>
    public static class FederatedCode
    {
        private static readonly Regex AggregateWord = new(@"\baggregate\b", RegexOptions.Compiled);

        /// <summary>
        /// The aggregate() function, for the review page's "How your result will be combined": from the line that starts
        /// with "def aggregate(" up to the next non-blank line at column 0 that is not a comment or decorator. Null when the
        /// code has no such line, or when anything else in it names aggregate (a second definition, an assignment, an
        /// import, a string): Python runs the last binding, so the block would otherwise show a function that may not run.
        /// The page then points to the full code.
        /// </summary>
        public static string? AggregateFunction(string? code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var lines = code.Replace("\r\n", "\n").Split('\n');
            var defs = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith("def aggregate(", StringComparison.Ordinal)).ToList();
            if (defs.Count != 1) return null;

            var start = defs[0];
            var end = start + 1;
            while (end < lines.Length)
            {
                var line = lines[end];
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#' && line[0] != '@') break;
                end++;
            }

            var elsewhere = lines.Take(start).Concat(lines.Skip(end));
            if (elsewhere.Any(l => AggregateWord.IsMatch(l.Split('#')[0]))) return null;

            return string.Join("\n", lines[start..end]).TrimEnd();
        }
    }
}
