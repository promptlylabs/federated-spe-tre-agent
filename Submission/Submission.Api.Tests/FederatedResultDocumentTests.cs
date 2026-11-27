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

    private const string AnalysisResult = "{\"kind\":\"analysis\",\"n\":120,\"value\":{\"mean_age\":41.2,\"n\":120}}";

    private static FederatedTask Row() => new()
    {
        TaskId = TaskGuid, TreName = "tre-a", StudyId = "study-1", Operation = "run-analysis", Dataset = "cohort_2024"
    };

    private static FederatedAnalysisResultDocument Doc() => new()
    {
        TaskId = TaskGuid.ToString("D"),
        Tre = "tre-a",
        StudyId = "study-1",
        Operation = "run-analysis",
        Dataset = "cohort_2024",
        DatasetSha256 = ShaOk,
        CompletedAt = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero),
        Result = new FederatedAnalysisResult
        {
            N = 120, Value = System.Text.Json.JsonDocument.Parse("{\"mean_age\":41.2}").RootElement.Clone()
        }
    };

    private static string Json(string taskId = "11111111-2222-3333-4444-555555555555", string tre = "tre-a",
        string studyId = "study-1", string operation = "run-analysis", string dataset = "cohort_2024",
        string extra = "", string result = AnalysisResult) =>
        $"{{\"schemaVersion\":1,\"taskId\":\"{taskId}\",\"tre\":\"{tre}\",\"studyId\":\"{studyId}\"," +
        $"\"operation\":\"{operation}\",\"dataset\":\"{dataset}\",\"datasetSha256\":\"{ShaOk}\",\"completedAt\":\"2026-11-04T10:00:00+00:00\"," +
        $"\"result\":{result}{extra}}}";

    private static FederatedResultValidation Validate(string json) =>
        FederatedResultDocumentJson.Validate(Encoding.UTF8.GetBytes(json), Row());

    [Fact]
    public void Serialize_writes_camelCase_and_schemaVersion_1()
    {
        var bytes = FederatedResultDocumentJson.SerializeAnalysis(Doc());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal((byte)'{', bytes[0]);
        Assert.Contains("\"schemaVersion\":1", text);
        Assert.Contains("\"taskId\":", text);
        Assert.Contains("\"datasetSha256\":", text);
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

    private const string CountTable =
        "{\"method\":\"crosstab\",\"statistic\":\"count\",\"rows\":[\"F\",\"M\"],\"columns\":[\"n\",\"s\"],\"count\":[[1,1],[1,0]]}";
    private const string MeanTable =
        "{\"method\":\"crosstab\",\"statistic\":\"mean\",\"rows\":[\"F\"],\"columns\":[\"n\"],\"count\":[[2]],\"sum\":[[70.5]]}";

    public static TheoryData<string, string?> AcroCases()
    {
        var label = new string('x', 1025);
        return new TheoryData<string, string?>
        {
            { $"{{\"tables\":[{CountTable},{MeanTable}]}}", null },
            { "{\"tables\":[]}", null },
            { $"{{\"tables\":[{CountTable.Replace("\"count\",\"rows\"", "\"median\",\"rows\"")}]}}", "result.acro.tables[0].statistic" },
            { $"{{\"tables\":[{CountTable.Replace("\"crosstab\"", "\"ols\"")}]}}", "result.acro.tables[0].method" },
            { $"{{\"tables\":[{CountTable.Replace("[[1,1],[1,0]]", "[[1,1],[1]]")}]}}", "result.acro.tables[0].count" },
            { $"{{\"tables\":[{CountTable.Replace("[[1,1],[1,0]]", "[[1,1],[1,-1]]")}]}}", "result.acro.tables[0].count" },
            { $"{{\"tables\":[{CountTable.Replace("[[1,1],[1,0]]", "[[1,1],[1,0.5]]")}]}}", "result.acro.tables[0].count" },
            { $"{{\"tables\":[{MeanTable.Replace(",\"sum\":[[70.5]]", "")}]}}", "result.acro.tables[0].sum" },
            { $"{{\"tables\":[{CountTable.Replace("]]}", "]],\"sum\":[[1,1],[1,0]]}")}]}}", "result.acro.tables[0].sum" },
            { $"{{\"tables\":[{CountTable.Replace("\"F\",\"M\"", $"\"F\",\"{label}\"")}]}}", "result.acro.tables[0].rows" },
            { $"{{\"tables\":[{string.Join(",", Enumerable.Repeat(CountTable, 65))}]}}", "result.acro.tables" },
            { $"{{\"tables\":[{CountTable.Replace("]]}", "]],\"other\":1}")}]}}", "result.acro.tables[0]" },
            { "{\"tables\":{}}", "result.acro.tables" },
        };
    }

    [Theory]
    [MemberData(nameof(AcroCases))]
    public void Validate_checks_the_acro_evidence(string acro, string? field)
    {
        var result = Validate(Json(result: AnalysisResult.Replace("}}", $"}},\"acro\":{acro}}}")));

        if (field is null)
        {
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(acro, result.AnalysisDocument!.Result.Acro!.Value.GetRawText());
        }
        else
        {
            Assert.False(result.IsValid);
            Assert.Contains($"'{field}'", result.Message);
            Assert.DoesNotContain("xxx", result.Message);
        }
    }

    [Fact]
    public void Serialize_leaves_out_acro_when_there_is_none()
    {
        Assert.DoesNotContain("acro", Encoding.UTF8.GetString(FederatedResultDocumentJson.SerializeAnalysis(Doc())));
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
        var row = Row();
        row.Dataset = new string('x', 1025);
        var json = Json(dataset: row.Dataset);

        var result = FederatedResultDocumentJson.Validate(Encoding.UTF8.GetBytes(json), row);

        Assert.False(result.IsValid);
        Assert.Contains("longer than 1024", result.Message);
    }

    [Fact]
    public void Validate_accepts_string_at_length_limit()
    {
        var row = Row();
        row.Dataset = new string('x', 1024);
        var json = Json(dataset: row.Dataset);

        var result = FederatedResultDocumentJson.Validate(Encoding.UTF8.GetBytes(json), row);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_rejects_missing_datasetSha256()
    {
        var result = Validate(Json().Replace($"\"datasetSha256\":\"{ShaOk}\",", ""));

        Assert.False(result.IsValid);
        Assert.Contains("datasetSha256", result.Message);
    }

    [Fact]
    public void Validate_rejects_null_datasetSha256()
    {
        var result = Validate(Json().Replace($"\"datasetSha256\":\"{ShaOk}\"", "\"datasetSha256\":null"));

        Assert.False(result.IsValid);
        Assert.Contains("datasetSha256", result.Message);
    }

    [Fact]
    public void Validate_rejects_bad_datasetSha256()
    {
        var doc = Doc();
        doc.DatasetSha256 = "ABC";

        var result = FederatedResultDocumentJson.Validate(FederatedResultDocumentJson.SerializeAnalysis(doc), Row());

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

    // The result: kind "analysis".

    private static FederatedResultValidation ValidateCode(string result) => Validate(Json(result: result));

    [Fact]
    public void Valid_analysis_document_passes()
    {
        var result = ValidateCode(AnalysisResult);

        Assert.True(result.IsValid, result.Message);
        var doc = result.AnalysisDocument!;
        Assert.Equal("analysis", doc.Result.Kind);
        Assert.Equal(120, doc.Result.N);
        Assert.Equal(41.2, doc.Result.Value.GetProperty("mean_age").GetDouble());
        Assert.Equal(ShaOk, result.DatasetSha256);
    }

    [Fact]
    public void SerializeAnalysis_round_trips_through_Validate()
    {
        var doc = new FederatedAnalysisResultDocument
        {
            TaskId = TaskGuid.ToString("D"),
            Tre = "tre-a",
            StudyId = "study-1",
            Operation = "run-analysis",
            Dataset = "cohort_2024",
            DatasetSha256 = ShaOk,
            CompletedAt = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero),
            Result = new FederatedAnalysisResult
            {
                N = 120, Value = System.Text.Json.JsonDocument.Parse("{\"mean_age\":41.2}").RootElement.Clone()
            }
        };

        var bytes = FederatedResultDocumentJson.SerializeAnalysis(doc);
        var result = FederatedResultDocumentJson.Validate(bytes, Row());

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(120, result.AnalysisDocument!.Result.N);
        Assert.Equal(41.2, result.AnalysisDocument.Result.Value.GetProperty("mean_age").GetDouble());
        Assert.StartsWith("{\"schemaVersion\":1,", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("{\"kind\":\"analysis\",\"value\":{}}", "missing required field 'result.n'")]
    [InlineData("{\"kind\":\"analysis\",\"n\":1}", "missing required field 'result.value'")]
    [InlineData("{\"kind\":\"analysis\",\"n\":1,\"value\":null}", "missing required field 'result.value'")]
    [InlineData("{\"kind\":\"analysis\",\"n\":-1,\"value\":{}}", "field 'result.n' must be a non-negative integer")]
    [InlineData("{\"kind\":\"analysis\",\"n\":\"1\",\"value\":{}}", "field 'result.n' must be a non-negative integer")]
    [InlineData("{\"kind\":\"analysis\",\"n\":1,\"value\":[1]}", "field 'result.value' must be a JSON object")]
    [InlineData("{\"kind\":\"scalar\",\"n\":1,\"value\":{}}", "result.kind must be 'analysis'")]
    public void Invalid_analysis_documents_fail(string result, string message)
    {
        var validation = ValidateCode(result);

        Assert.False(validation.IsValid);
        Assert.Equal(message, validation.Message);
    }

    [Fact]
    public void Analysis_n_must_match_the_dicts_own_n()
    {
        var validation = ValidateCode("{\"kind\":\"analysis\",\"n\":1000,\"value\":{\"n\":3,\"mean_age\":40}}");

        Assert.False(validation.IsValid);
        Assert.Equal("field 'result.n' does not match result.value.n", validation.Message);
    }

    [Fact]
    public void Unknown_field_in_analysis_result_fails()
    {
        var validation = ValidateCode("{\"kind\":\"analysis\",\"n\":1,\"value\":{},\"extra\":1}");

        Assert.False(validation.IsValid);
    }

    [Theory]
    [InlineData("120.0")]
    [InlineData("1.2e2")]
    public void Analysis_n_must_be_an_integer(string n)
    {
        var validation = ValidateCode($"{{\"kind\":\"analysis\",\"n\":{n},\"value\":{{}}}}");

        Assert.False(validation.IsValid);
        Assert.Equal("field 'result.n' must be a non-negative integer", validation.Message);
    }

    [Fact]
    public void Validate_ScalarDocument_Invalid()
    {
        // A well-formed scalar document, as the removed count/mean tasks wrote it.
        const string scalar = "{\"kind\":\"scalar\",\"value\":1.5,\"rowCount\":3}";
        var forCodeRow = ValidateCode(scalar);
        // Also for a row stored before count/mean were removed.
        var oldRow = Row();
        oldRow.Operation = "mean";
        var forOldRow = FederatedResultDocumentJson.Validate(
            Encoding.UTF8.GetBytes(Json(operation: "mean", result: scalar)), oldRow);

        Assert.False(forCodeRow.IsValid);
        Assert.Equal("result.kind must be 'analysis'", forCodeRow.Message);
        Assert.False(forOldRow.IsValid);
    }

    [Fact]
    public void Analysis_document_operation_must_match_the_task()
    {
        var validation = FederatedResultDocumentJson.Validate(
            Encoding.UTF8.GetBytes(Json(operation: "mean", result: AnalysisResult)), Row());

        Assert.False(validation.IsValid);
        Assert.Equal("field 'operation' does not match the task", validation.Message);
    }
}
