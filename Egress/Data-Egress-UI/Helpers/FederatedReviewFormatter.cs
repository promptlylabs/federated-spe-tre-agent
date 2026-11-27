using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FiveSafesTes.Core.Models;

namespace Data_Egress_UI.Helpers
{
    /// <summary>Formatting for the federated review block on the egress page.</summary>
    public static class FederatedReviewFormatter
    {
        /// <summary>Indents the result JSON; returns the raw text when it is not valid JSON.</summary>
        public static string PrettyPrintJson(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    doc.WriteTo(writer);
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        /// <summary>The label/value rows to show for the study; fields that are null are left out.</summary>
        public static List<(string Label, string Value)> StudyRows(FederatedStudy study)
        {
            var rows = new List<(string, string)>();
            Add(rows, "Name", study.Name);
            Add(rows, "Researcher", study.Researcher);
            var p = study.DataPermit;
            if (p != null)
            {
                Add(rows, "Permit identifier", p.Identifier);
                Add(rows, "Issuer", p.Issuer);
                Add(rows, "Purpose", p.Purpose);
                Add(rows, "Principal investigator", p.PrincipalInvestigator);
                Add(rows, "Authorised persons", Join(p.AuthorisedPersons));
                Add(rows, "Data categories", Join(p.DataCategories));
                Add(rows, "Valid from", p.ValidFrom?.ToString("yyyy-MM-dd"));
                Add(rows, "Valid until", p.ValidUntil?.ToString("yyyy-MM-dd"));
                Add(rows, "Conditions", p.Conditions);
            }
            return rows;
        }

        /// <summary>
        /// The researcher's aggregate() function, for the "How your result will be combined" block: from the line that starts
        /// with "def aggregate(" up to the next non-blank line at column 0 that is not a comment or decorator. Null when the
        /// code has no such line. The full code is shown above it, so a function this misses is still reviewable.
        /// </summary>
        public static string? AggregateFunction(string? code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var lines = code.Replace("\r\n", "\n").Split('\n');
            var start = Array.FindIndex(lines, l => l.StartsWith("def aggregate(", StringComparison.Ordinal));
            if (start < 0) return null;
            var end = start + 1;
            while (end < lines.Length)
            {
                var line = lines[end];
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#' && line[0] != '@') break;
                end++;
            }

            return string.Join("\n", lines[start..end]).TrimEnd();
        }

        /// <summary>True when the orchestrator sent no study details and no data permit.</summary>
        public static bool NothingProvided(FederatedStudy? study) =>
            study == null || StudyRows(study).Count == 0;

        private static string? Join(string[]? values) =>
            values is { Length: > 0 } ? string.Join(", ", values) : null;

        private static void Add(List<(string, string)> rows, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value));
        }
    }
}
