using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// Outcome of validating a result file: the <see cref="AnalysisDocument"/>, or a <see cref="Message"/>.
    /// </summary>
    public sealed record FederatedResultValidation(FederatedAnalysisResultDocument? AnalysisDocument, string? Message)
    {
        public bool IsValid => AnalysisDocument != null;

        public string? DatasetSha256 => AnalysisDocument?.DatasetSha256;
    }

    public static class FederatedResultDocumentJson
    {
        private const int MaxStringLength = FederatedResultLimits.MaxStringLength;
        private const string NotValidJsonMessage =
            "result file is not valid JSON (malformed UTF-8 or JSON, a duplicate property, or nesting deeper than 16 levels)";

        private static readonly Regex Sha256Pattern = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions SerializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private static readonly JsonSerializerOptions ParseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            MaxDepth = FederatedResultLimits.MaxJsonDepth
        };

        /// <summary>The result document of a code task; UTF-8, no BOM, not indented.</summary>
        public static byte[] SerializeAnalysis(FederatedAnalysisResultDocument doc) =>
            JsonSerializer.SerializeToUtf8Bytes(doc, SerializeOptions);

        public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static readonly string[] RequiredTopFields =
            ["schemaVersion", "taskId", "tre", "studyId", "operation", "dataset", "datasetSha256", "completedAt", "result"];

        /// <summary>
        /// Validates a result file against the task row: an "analysis" result whose identity fields match the row.
        /// Every other document, an old "scalar" one included, is invalid. Messages are fixed text plus field names
        /// only; they never include file content.
        /// </summary>
        public static FederatedResultValidation Validate(byte[] bytes, FederatedTask row)
        {
            JsonElement root;
            try
            {
                using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = FederatedResultLimits.MaxJsonDepth,
                    AllowDuplicateProperties = false
                });
                root = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Invalid(NotValidJsonMessage);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("result file is not a JSON object");
            }

            if (MissingField(root, RequiredTopFields) is { } missingTop)
            {
                return Invalid($"missing required field '{missingTop}'");
            }

            var result = root.GetProperty("result");
            if (result.ValueKind != JsonValueKind.Object)
            {
                return Invalid("field 'result' must be a JSON object");
            }

            // The kind first, so a document of the wrong kind is reported as that and not as a missing field.
            if (MissingField(result, ["kind"]) is not null)
            {
                return Invalid("missing required field 'result.kind'");
            }

            var kind = result.GetProperty("kind");
            if (kind.ValueKind != JsonValueKind.String || kind.GetString() != "analysis")
            {
                return Invalid("result.kind must be 'analysis'");
            }

            if (MissingField(result, ["n", "value"]) is { } missingResult)
            {
                return Invalid($"missing required field 'result.{missingResult}'");
            }

            // n is read from the JSON, not the typed document, so 120.0 is "not an integer" rather than "not JSON".
            var n = result.GetProperty("n");
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt64(out var count) || count < 0)
            {
                return Invalid("field 'result.n' must be a non-negative integer");
            }

            if (result.GetProperty("value").ValueKind != JsonValueKind.Object)
            {
                return Invalid("field 'result.value' must be a JSON object");
            }

            // The runner copies n from the researcher's dict; a different value would misstate how many people it covers.
            if (result.GetProperty("value").TryGetProperty("n", out var valueN)
                && !(valueN.ValueKind == JsonValueKind.Number && valueN.TryGetInt64(out var dictN) && dictN == count))
            {
                return Invalid("field 'result.n' does not match result.value.n");
            }

            FederatedAnalysisResultDocument? doc;
            try
            {
                // Rejects unknown and mistyped fields.
                doc = JsonSerializer.Deserialize<FederatedAnalysisResultDocument>(bytes, ParseOptions);
            }
            catch (JsonException)
            {
                return Invalid(NotValidJsonMessage);
            }

            if (doc is null)
            {
                return Invalid("result file is not a JSON object");
            }

            if (doc.SchemaVersion != 1)
            {
                return Invalid("schemaVersion must be 1");
            }

            var strings = new (string Name, string? Value)[]
            {
                ("taskId", doc.TaskId), ("tre", doc.Tre), ("studyId", doc.StudyId), ("operation", doc.Operation),
                ("dataset", doc.Dataset), ("datasetSha256", doc.DatasetSha256), ("result.kind", doc.Result.Kind)
            };
            if (TooLong(strings) is { } tooLong)
            {
                return Invalid(tooLong);
            }

            if (doc.Result.Kind != "analysis")
            {
                return Invalid("result.kind must be 'analysis'");
            }

            if (!Sha256Pattern.IsMatch(doc.DatasetSha256!))
            {
                return Invalid("field 'datasetSha256' is not a lowercase hex SHA-256");
            }

            if (Mismatch(doc.TaskId, doc.Tre, doc.StudyId, doc.Operation, doc.Dataset, row) is { } mismatch)
            {
                return Invalid(mismatch);
            }

            return new FederatedResultValidation(doc, null);
        }

        /// <summary>The first of <paramref name="names"/> that is absent or null in <paramref name="obj"/>.</summary>
        private static string? MissingField(JsonElement obj, IEnumerable<string> names) =>
            names.FirstOrDefault(name => !obj.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null);

        private static string? TooLong(IEnumerable<(string Name, string? Value)> strings)
        {
            foreach (var (name, value) in strings)
            {
                if (value is { Length: > MaxStringLength })
                {
                    return $"field '{name}' is longer than {MaxStringLength} characters";
                }
            }

            return null;
        }

        private static string? Mismatch(string taskIdText, string tre, string studyId, string operation, string dataset, FederatedTask row)
        {
            if (!Guid.TryParse(taskIdText, out var taskId) || taskId != row.TaskId)
            {
                return "field 'taskId' does not match the task";
            }

            if (tre != row.TreName)
            {
                return "field 'tre' does not match the task";
            }

            if (studyId != row.StudyId)
            {
                return "field 'studyId' does not match the task";
            }

            if (operation != row.Operation)
            {
                return "field 'operation' does not match the task";
            }

            if (dataset != row.Dataset)
            {
                return "field 'dataset' does not match the task";
            }

            return null;
        }

        private static FederatedResultValidation Invalid(string message) => new(null, message);
    }
}
