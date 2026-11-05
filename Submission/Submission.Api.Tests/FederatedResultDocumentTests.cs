using System.Text;
using FiveSafesTes.Core.Models.FederatedTasks;
using Xunit;

namespace Submission.Api.Tests;

public class FederatedResultDocumentTests
{
    private static readonly Guid TaskGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ShaOk = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ParseMessage =
        "result file is not valid JSON (malformed UTF-8 or JSON, a duplicate property, or nesting deeper than 16 levels)";

    private static FederatedTask Row() => new()
    {
        TaskId = TaskGuid, TreName = "tre-a", StudyId = "study-1", Operation = "mean", Dataset = "cohort_2024"
    };

    private static FederatedResultDocument Doc() => new()
    {
        TaskId = TaskGuid.ToString("D"),
        Tre = "tre-a",
        StudyId = "study-1",
        Operation = "mean",
        Dataset = "cohort_2024",
        DatasetSha256 = ShaOk,
        CompletedAt = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero),
        Result = new FederatedScalarResult { Value = 1.5m, RowCount = 3 }
    };

    private static string Json(string taskId = "11111111-2222-3333-4444-555555555555", string tre = "tre-a",
        string studyId = "study-1", string operation = "mean", string dataset = "cohort_2024",
        string extra = "", string result = "{\"kind\":\"scalar\",\"value\":1.5,\"rowCount\":3}") =>
        $"{{\"schemaVersion\":1,\"taskId\":\"{taskId}\",\"tre\":\"{tre}\",\"studyId\":\"{studyId}\"," +
        $"\"operation\":\"{operation}\",\"dataset\":\"{dataset}\",\"completedAt\":\"2026-11-04T10:00:00+00:00\"," +
        $"\"result\":{result}{extra}}}";

    private static FederatedResultValidation Validate(string json) =>
        FederatedResultDocumentJson.Validate(Encoding.UTF8.GetBytes(json), Row());

    [Fact]
    public void Serialize_round_trips_full_precision_decimal()
    {
        var doc = Doc();
        doc.Result.Value = 35.333333333333333333333333333m;

        var result = FederatedResultDocumentJson.Validate(FederatedResultDocumentJson.Serialize(doc), Row());

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(35.333333333333333333333333333m, result.Document!.Result.Value);
    }

    [Fact]
    public void Serialize_writes_camelCase_and_schemaVersion_1()
    {
        var bytes = FederatedResultDocumentJson.Serialize(Doc());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal((byte)'{', bytes[0]);
        Assert.Contains("\"schemaVersion\":1", text);
        Assert.Contains("\"taskId\":", text);
        Assert.Contains("\"rowCount\":", text);
    }

    [Fact]
    public void Validate_accepts_null_value()
    {
        var doc = Doc();
        doc.Result.Value = null;
        doc.Result.RowCount = 0;

        Assert.True(FederatedResultDocumentJson.Validate(FederatedResultDocumentJson.Serialize(doc), Row()).IsValid);
    }

    [Fact]
    public void Validate_rejects_duplicate_property()
    {
        var result = Validate(Json().Replace("{\"schemaVersion\":1,", "{\"schemaVersion\":1,\"schemaVersion\":1,"));

        Assert.False(result.IsValid);
        Assert.Equal(ParseMessage, result.Message);
    }

    [Fact]
    public void Validate_rejects_depth_over_limit()
    {
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 17)) + "1" + new string('}', 17);
        var result = Validate(Json(result: nested));

        Assert.False(result.IsValid);
        Assert.Equal(ParseMessage, result.Message);
    }

    [Fact]
    public void Validate_rejects_invalid_utf8()
    {
        var result = FederatedResultDocumentJson.Validate([0xFF, 0xFE], Row());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_unknown_schemaVersion()
    {
        var result = Validate(Json().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_missing_required_field()
    {
        var result = Validate(Json().Replace("\"dataset\":\"cohort_2024\",", ""));

        Assert.False(result.IsValid);
        Assert.Contains("dataset", result.Message);
    }

    [Theory]
    [InlineData("taskId")]
    [InlineData("tre")]
    [InlineData("studyId")]
    [InlineData("operation")]
    [InlineData("dataset")]
    public void Validate_rejects_identity_mismatch(string field)
    {
        var json = field switch
        {
            "taskId" => Json(taskId: "99999999-2222-3333-4444-555555555555"),
            "tre" => Json(tre: "other"),
            "studyId" => Json(studyId: "other"),
            "operation" => Json(operation: "sum"),
            _ => Json(dataset: "other")
        };

        var result = Validate(json);

        Assert.False(result.IsValid);
        Assert.Contains(field, result.Message);
    }

    [Fact]
    public void Validate_rejects_overlong_string()
    {
        var result = Validate(Json(dataset: new string('x', 1025)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_bad_datasetSha256()
    {
        var doc = Doc();
        doc.DatasetSha256 = "ABC";

        var result = FederatedResultDocumentJson.Validate(FederatedResultDocumentJson.Serialize(doc), Row());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_message_never_echoes_content()
    {
        var result = Validate(Json(dataset: "SECRET-xyz"));

        Assert.False(result.IsValid);
        Assert.DoesNotContain("SECRET-xyz", result.Message);
    }

    [Fact]
    public void IsValidErrorMessage_rules()
    {
        Assert.True(FederatedResultLimits.IsValidErrorMessage(new string('a', 512)));
        Assert.False(FederatedResultLimits.IsValidErrorMessage(new string('a', 513)));
        Assert.False(FederatedResultLimits.IsValidErrorMessage("a\nb"));
        Assert.True(FederatedResultLimits.IsValidErrorMessage(null));
    }

    [Fact]
    public void ErrorCodePattern_rules()
    {
        Assert.Matches(FederatedResultLimits.ErrorCodePattern, "invalid_task");
        Assert.DoesNotMatch(FederatedResultLimits.ErrorCodePattern, "Invalid");
        Assert.DoesNotMatch(FederatedResultLimits.ErrorCodePattern, "a-b");
        Assert.DoesNotMatch(FederatedResultLimits.ErrorCodePattern, new string('a', 65));
    }
}
