using System.Text.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// The pooled result of a study version: what the researcher's aggregate() returned, the pooled ACRO tables and the
    /// small-number checks applied. <see cref="Sites"/> is how many
    /// hospital results went in, never which. Stored under <c>studies/&lt;versionId&gt;/&lt;sha256&gt;.json</c>.
    /// </summary>
    public sealed class FederatedPooledResultDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string StudyId { get; set; } = string.Empty;
        public int Version { get; set; }
        public Guid VersionId { get; set; }
        public string CodeSha256 { get; set; } = string.Empty;
        public int Sites { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public FederatedPooledResult Result { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            MaxDepth = FederatedResultLimits.MaxJsonDepth
        };

        /// <summary>UTF-8, no BOM, not indented.</summary>
        public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

        /// <summary>
        /// Checks a pooled document against the version it is for: the same limits as a site result document, kind
        /// "aggregate", a non-negative integer n equal to value.n, and identity fields and the number of released hospitals
        /// that match. Returns null when valid, else a fixed message naming fields only, never content.
        /// </summary>
        public static string? Validate(byte[] bytes, FederatedStudyVersion version, int releasedSites)
        {
            if (bytes.Length > FederatedResultLimits.MaxResultBytes)
            {
                return $"the pooled result must be at most {FederatedResultLimits.MaxResultBytes} bytes";
            }

            FederatedPooledResultDocument? doc;
            JsonElement root;
            try
            {
                using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = FederatedResultLimits.MaxJsonDepth, AllowDuplicateProperties = false });
                root = parsed.RootElement.Clone();
                doc = JsonSerializer.Deserialize<FederatedPooledResultDocument>(bytes, Options);
            }
            catch (JsonException)
            {
                return "pooled result is not valid JSON of the expected shape";
            }

            if (doc is null || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return "pooled result is not a JSON object with a result";
            }

            if (doc.SchemaVersion != 1) return "schemaVersion must be 1";
            if (doc.Result.Kind != "aggregate") return "result.kind must be 'aggregate'";
            if (!result.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt64(out var count) || count < 0)
            {
                return "field 'result.n' must be a non-negative integer";
            }

            if (doc.Result.Value.ValueKind != JsonValueKind.Object)
            {
                return "field 'result.value' must be a JSON object";
            }

            if (!doc.Result.Value.TryGetProperty("n", out var valueN) || !valueN.TryGetInt64(out var dictN) || dictN != count)
            {
                return "field 'result.n' does not match result.value.n";
            }

            if (doc.Result.Tables.ValueKind != JsonValueKind.Array)
            {
                return "field 'result.tables' must be an array";
            }

            if (doc.Result.Checks is not { Minimum: >= 1, WithheldCells: >= 0, ValueChecked: false })
            {
                return "field 'result.checks' must give the minimum, the withheld cells and valueChecked false";
            }

            if (doc.VersionId != version.VersionId || doc.StudyId != version.StudyId || doc.Version != version.Version
                || doc.CodeSha256 != version.CodeSha256)
            {
                return "the pooled result is not for this study version";
            }

            if (doc.Sites != releasedSites)
            {
                return "field 'sites' is not the number of hospitals that released a result";
            }

            return null;
        }
    }

    public sealed class FederatedPooledResult
    {
        public string Kind { get; set; } = "aggregate";
        public long N { get; set; }

        /// <summary>What aggregate() returned. Not checked for small numbers (<see cref="FederatedPooledChecks.ValueChecked"/>).</summary>
        public JsonElement Value { get; set; }

        /// <summary>The pooled ACRO tables, with the cells below the minimum withheld (null); [] when there are none.</summary>
        public JsonElement Tables { get; set; }

        public FederatedPooledChecks? Checks { get; set; }
    }

    /// <summary>The small-number checks the aggregator applied to the pooled result.</summary>
    public sealed class FederatedPooledChecks
    {
        /// <summary>The fewest people a pooled cell (and the pooled result) may be based on.</summary>
        public int Minimum { get; set; }

        /// <summary>How many cells of the pooled ACRO tables were withheld.</summary>
        public int WithheldCells { get; set; }

        /// <summary>Always false: aggregate()'s values are not checked for small numbers; a person reviews them.</summary>
        public bool ValueChecked { get; set; }
    }
}
