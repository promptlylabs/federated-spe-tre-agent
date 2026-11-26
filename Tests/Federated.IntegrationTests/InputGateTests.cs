using System.Text.Json.Nodes;
using Xunit;

namespace Federated.IntegrationTests;

/// <summary>Tasks the input gates stop: the hospital's input checks, or Submission's task profile.</summary>
[Collection(StackCollection.Name)]
public class InputGateTests
{
    private readonly StackFixture stack;

    public InputGateTests(StackFixture stack) => this.stack = stack;

    [SkippableFact]
    public async Task Hospital_input_checks_stop_a_task_for_a_dataset_the_hospital_does_not_hold()
    {
        stack.EnsureReady();
        var queued = await stack.Submission.EnqueueCodeAsync($"missing_{Guid.NewGuid():N}", Code.CountRows);
        Assert.Equal(200, queued.Status);

        var end = await Await.UntilSettledAsync(stack, queued.Id!, Await.PhaseWait);

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("rejected_by_input_checks", end.Phase);
        Assert.Equal("dataset_not_found", end.ErrorCode);
        Assert.Null(await stack.Submission.GetResultAsync(queued.Id!));
        Assert.False(await stack.Results.HasResultAsync(queued.Id!));
    }

    [SkippableFact]
    public async Task Hospital_input_checks_stop_a_task_whose_dataset_folder_holds_two_files()
    {
        stack.EnsureReady();
        var dataset = $"ambiguous_{Guid.NewGuid():N}";
        try
        {
            await stack.Storage.PutDatasetFileAsync(dataset, "a.csv", "age,sex\n30,F\n");
            await stack.Storage.PutDatasetFileAsync(dataset, "b.csv", "age,sex\n40,M\n");
            var queued = await stack.Submission.EnqueueCodeAsync(dataset, Code.CountRows);
            Assert.Equal(200, queued.Status);

            var end = await Await.UntilSettledAsync(stack, queued.Id!, Await.PhaseWait);

            Assert.Equal("CANCELED", end.State);
            Assert.Equal("rejected_by_input_checks", end.Phase);
            Assert.Equal("ambiguous_dataset", end.ErrorCode);
            Assert.Null(await stack.Submission.GetResultAsync(queued.Id!));
            Assert.False(await stack.Results.HasResultAsync(queued.Id!));
        }
        finally
        {
            await stack.Storage.DeleteDatasetAsync(dataset);
        }
    }

    [SkippableFact]
    public async Task Unsafe_code_is_refused_before_the_dataset_is_read()
    {
        stack.EnsureReady();
        var queued = await stack.Submission.EnqueueCodeAsync("cohort_2024", Code.OpenSocket);
        Assert.Equal(200, queued.Status);

        var end = await Await.UntilSettledAsync(stack, queued.Id!, Await.PhaseWait);

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("rejected_by_input_checks", end.Phase);
        Assert.Equal("unsafe_code", end.ErrorCode);
        Assert.Contains("import-not-allowed line 1", end.ErrorMessage);
        Assert.Equal("2026-11-2", end.ScanRulesVersion);
        Assert.Null(await stack.Submission.GetResultAsync(queued.Id!));
        Assert.False(await stack.Results.HasResultAsync(queued.Id!));
    }

    [SkippableTheory]
    [InlineData("old count task")]
    [InlineData("foreign image")]
    [InlineData("old tag names")]
    [InlineData("code that does not match its hash")]
    public async Task Submission_refuses_a_task_outside_the_federated_profile(string kind)
    {
        stack.EnsureReady();
        var body = kind switch
        {
            "old count task" => OldCountTask("cohort_2024"),
            "foreign image" => SubmissionClient.CodeTask("cohort_2024", Code.CountRows, image: "python:3.12"),
            "old tag names" => WithTags(SubmissionClient.CodeTask("cohort_2024", Code.CountRows), new JsonObject
            {
                ["tres"] = StackSettings.Tre, ["study_id"] = "study-001", ["fed.code_sha256"] = SubmissionClient.CodeSha256(Code.CountRows)
            }),
            _ => WithTags(SubmissionClient.CodeTask("cohort_2024", Code.CountRows), new JsonObject
            {
                ["fed.sites"] = StackSettings.BothHospitals, ["fed.study_id"] = "study-001", ["fed.code_sha256"] = SubmissionClient.CodeSha256(Code.MeanAge)
            })
        };

        var outcome = await stack.Submission.EnqueueBodyAsync(body);

        Assert.Equal(400, outcome.Status);
        Assert.Equal("invalid_task", outcome.ErrorCode);
        Assert.Null(outcome.Id);
    }

    private static JsonObject WithTags(JsonObject task, JsonObject tags)
    {
        task["tags"] = tags;
        return task;
    }

    /// <summary>A count task as Submission accepted it before every task became a code task.</summary>
    private static JsonObject OldCountTask(string dataset) => new()
    {
        ["executors"] = new JsonArray(new JsonObject { ["image"] = "5s-tes/federated-ops:1", ["command"] = new JsonArray("count") }),
        ["inputs"] = new JsonArray(new JsonObject { ["url"] = $"dataset://{dataset}", ["path"] = $"/data/{dataset}", ["type"] = "DIRECTORY" }),
        ["outputs"] = new JsonArray(),
        ["tags"] = SubmissionClient.FederationTags()
    };
}
