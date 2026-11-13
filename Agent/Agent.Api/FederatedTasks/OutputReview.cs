using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>schema.org action statuses used by a Five Safes RO-Crate DisclosureCheck.</summary>
    public static class DisclosureCheckStatus
    {
        public const string Potential = "http://schema.org/PotentialActionStatus";
        public const string Completed = "http://schema.org/CompletedActionStatus";
        public const string Failed = "http://schema.org/FailedActionStatus";
    }

    /// <summary>
    /// The hospital reviewer's decision on a federated task result, shaped as a DisclosureCheck (AssessAction).
    /// </summary>
    public sealed record OutputReview(
        string ResultSha256,
        DateTimeOffset StartTime,
        string ActionStatus,
        string? Agent = null,
        DateTimeOffset? EndTime = null,
        string? Description = null)
    {
        public bool IsPending => ActionStatus == DisclosureCheckStatus.Potential;
        public bool IsApproved => ActionStatus == DisclosureCheckStatus.Completed;
        public bool IsRejected => ActionStatus == DisclosureCheckStatus.Failed;

        public static OutputReview Pending(string sha, DateTimeOffset now) =>
            new(sha, now, DisclosureCheckStatus.Potential);
    }

    public static class OutputReviewJson
    {
        private const string AssessActionType = "AssessAction";
        private const string DisclosureCheckType = "https://w3id.org/shp#DisclosureCheck";

        public static byte[] Serialize(OutputReview r)
        {
            var obj = new JsonObject
            {
                ["@type"] = AssessActionType,
                ["additionalType"] = DisclosureCheckType,
                ["actionStatus"] = r.ActionStatus,
                ["object"] = new JsonObject { ["sha256"] = r.ResultSha256 },
                ["startTime"] = r.StartTime.ToString("O"),
                ["endTime"] = r.EndTime?.ToString("O"),
                ["agent"] = r.Agent,
                ["description"] = r.Description
            };
            return JsonSerializer.SerializeToUtf8Bytes(obj);
        }

        /// <summary>Reads a review record. A "registered" field written by older versions is ignored.</summary>
        public static OutputReview Deserialize(byte[] json)
        {
            var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Review is not a JSON object.");

            if (root["@type"]?.GetValue<string>() != AssessActionType)
                throw new JsonException("Review is not an AssessAction.");

            var status = root["actionStatus"]?.GetValue<string>();
            if (status is not (DisclosureCheckStatus.Potential or DisclosureCheckStatus.Completed or DisclosureCheckStatus.Failed))
                throw new JsonException($"Unknown actionStatus '{status}'.");

            var sha = root["object"]?["sha256"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sha))
                throw new JsonException("Review has no result sha256.");

            var start = root["startTime"]?.GetValue<string>()
                ?? throw new JsonException("Review has no startTime.");
            var end = root["endTime"]?.GetValue<string>();

            return new OutputReview(
                sha,
                DateTimeOffset.Parse(start, null, System.Globalization.DateTimeStyles.RoundtripKind),
                status,
                root["agent"]?.GetValue<string>(),
                end is null ? null : DateTimeOffset.Parse(end, null, System.Globalization.DateTimeStyles.RoundtripKind),
                root["description"]?.GetValue<string>());
        }
    }
}
