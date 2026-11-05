using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    public sealed record FederatedResultValidation(FederatedResultDocument? Document, string? Message)
    {
        public bool IsValid => Document != null;
    }

    public static class FederatedResultDocumentJson
    {
        private const int MaxStringLength = 1024;
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

        /// <summary>UTF-8, no BOM, not indented.</summary>
        public static byte[] Serialize(FederatedResultDocument doc) =>
            JsonSerializer.SerializeToUtf8Bytes(doc, SerializeOptions);

        public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        /// <summary>
        /// Validates a result file against the task row. Messages are fixed text plus field names only; they never
        /// include file content.
        /// </summary>
        public static FederatedResultValidation Validate(byte[] bytes, FederatedTask row)
        {
            FederatedResultDocument? doc;
            JsonElement root;
            try
            {
                using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = FederatedResultLimits.MaxJsonDepth,
                    AllowDuplicateProperties = false
                });
                root = parsed.RootElement.Clone();
                doc = JsonSerializer.Deserialize<FederatedResultDocument>(bytes, ParseOptions);
            }
            catch (JsonException)
            {
                return Invalid(NotValidJsonMessage);
            }

            if (doc is null || root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("result file is not a JSON object");
            }

            foreach (var name in new[] { "schemaVersion", "taskId", "tre", "studyId", "operation", "dataset", "datasetSha256", "completedAt", "result" })
            {
                if (!root.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
                {
                    return Invalid($"missing required field '{name}'");
                }
            }

            if (doc.SchemaVersion != 1)
            {
                return Invalid("unsupported schemaVersion");
            }

            if (doc.Result is null)
            {
                return Invalid("missing required field 'result'");
            }

            foreach (var name in new[] { "kind", "rowCount" })
            {
                if (!root.GetProperty("result").TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
                {
                    return Invalid($"missing required field 'result.{name}'");
                }
            }

            var strings = new (string Name, string? Value)[]
            {
                ("taskId", doc.TaskId), ("tre", doc.Tre), ("studyId", doc.StudyId), ("operation", doc.Operation),
                ("dataset", doc.Dataset), ("datasetSha256", doc.DatasetSha256), ("result.kind", doc.Result.Kind)
            };
            foreach (var (name, value) in strings)
            {
                if (value is { Length: > MaxStringLength })
                {
                    return Invalid($"field '{name}' is longer than {MaxStringLength} characters");
                }
            }

            if (doc.Result.Kind != "scalar")
            {
                return Invalid("unsupported result.kind");
            }

            if (doc.DatasetSha256 is not null && !Sha256Pattern.IsMatch(doc.DatasetSha256))
            {
                return Invalid("field 'datasetSha256' is not a lowercase hex SHA-256");
            }

            if (!Guid.TryParse(doc.TaskId, out var taskId) || taskId != row.TaskId)
            {
                return Invalid("field 'taskId' does not match the task");
            }

            if (doc.Tre != row.TreName)
            {
                return Invalid("field 'tre' does not match the task");
            }

            if (doc.StudyId != row.StudyId)
            {
                return Invalid("field 'studyId' does not match the task");
            }

            if (doc.Operation != row.Operation)
            {
                return Invalid("field 'operation' does not match the task");
            }

            if (doc.Dataset != row.Dataset)
            {
                return Invalid("field 'dataset' does not match the task");
            }

            return new FederatedResultValidation(doc, null);
        }

        private static FederatedResultValidation Invalid(string message) => new(null, message);
    }
}
