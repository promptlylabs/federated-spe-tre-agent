using System.Text;
using System.Text.Json;
using BL.Models;

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
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
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
