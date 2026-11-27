using System.Text.Json.Nodes;
using Xunit;

namespace Federated.IntegrationTests;

/// <summary>
/// The standard GA4GH TES routes for federated studies (ANZE-6134), called as DAAMS calls them: submit, follow and cancel a
/// study version; refusals store nothing; a repeated fed.request_id gives the same study; no anonymous access.
/// </summary>
[Collection(StackCollection.Name)]
public class TesApiTests
{
    private readonly StackFixture stack;

    public TesApiTests(StackFixture stack) => this.stack = stack;

    [SkippableFact]
    public async Task A_study_is_submitted_followed_and_cancelled_through_TES()
    {
        stack.EnsureReady();

        var (created, body) = await stack.Submission.TesAsync(HttpMethod.Post, "/v1/tasks", SubmissionClient.CodeTask("cohort_2024", Code.CountRows));
        Assert.Equal(200, created);
        var id = body!["id"]!.GetValue<string>();

        var (got, task) = await stack.Submission.TesAsync(HttpMethod.Get, $"/v1/tasks/{id}?view=BASIC");
        Assert.Equal(200, got);
        Assert.Equal("RUNNING", task!["state"]!.GetValue<string>());
        var metadata = task["logs"]![0]!["metadata"]!;
        Assert.NotNull(metadata[$"hospital.{StackSettings.Tre}.task_id"]);
        Assert.NotNull(metadata[$"hospital.{StackSettings.OtherTre}.task_id"]);
        Assert.Null(task["inputs"]![0]!["content"]);   // BASIC leaves the code out

        var (cancelled, _) = await stack.Submission.TesAsync(HttpMethod.Post, $"/v1/tasks/{id}:cancel");
        Assert.Equal(200, cancelled);
        var (_, after) = await stack.Submission.TesAsync(HttpMethod.Get, $"/v1/tasks/{id}");
        Assert.Contains(after!["state"]!.GetValue<string>(), new[] { "CANCELING", "CANCELED" });
    }

    [SkippableFact]
    public async Task An_unapproved_image_is_refused_and_nothing_stored()
    {
        stack.EnsureReady();
        var (_, before) = await stack.Submission.TesAsync(HttpMethod.Get, "/v1/tasks?view=MINIMAL&page_size=2047");

        var (status, error) = await stack.Submission.TesAsync(HttpMethod.Post, "/v1/tasks",
            SubmissionClient.CodeTask("cohort_2024", Code.CountRows, image: "python:3.12"));

        Assert.Equal((400, "invalid_task"), (status, error!["error_code"]!.GetValue<string>()));
        var (_, after) = await stack.Submission.TesAsync(HttpMethod.Get, "/v1/tasks?view=MINIMAL&page_size=2047");
        Assert.Equal(before!["tasks"]!.AsArray().Count, after!["tasks"]!.AsArray().Count);
    }

    [SkippableFact]
    public async Task A_repeated_request_id_gives_the_same_study()
    {
        stack.EnsureReady();
        var task = SubmissionClient.CodeTask("cohort_2024", Code.CountRows);
        task["tags"]!["fed.request_id"] = $"daams-{Guid.NewGuid():N}";

        var (_, first) = await stack.Submission.TesAsync(HttpMethod.Post, "/v1/tasks", task);
        var (status, again) = await stack.Submission.TesAsync(HttpMethod.Post, "/v1/tasks", JsonNode.Parse(task.ToJsonString())!.AsObject());

        Assert.Equal(200, status);
        Assert.Equal(first!["id"]!.GetValue<string>(), again!["id"]!.GetValue<string>());
        await stack.Submission.TesAsync(HttpMethod.Post, $"/v1/tasks/{first["id"]!.GetValue<string>()}:cancel");
    }

    [SkippableFact]
    public async Task Anonymous_callers_are_refused()
    {
        stack.EnsureReady();

        var (status, _) = await stack.Submission.TesAsync(HttpMethod.Get, "/v1/tasks", anonymous: true);

        Assert.Equal(401, status);
    }

    /// <summary>
    /// A hospital's human admin (role dare-tre-admin, not the TRE's Agent service account) gets neither a hospital's view nor
    /// the orchestrator admin's view of every study.
    /// </summary>
    [SkippableFact]
    public async Task A_hospitals_human_admin_cannot_read_studies()
    {
        stack.EnsureReady();

        Assert.Equal(403, await stack.Submission.TesStatusAsAsync("tre-agent-my-tre", "TreAgent1!", "/v1/tasks?view=FULL"));
    }
}
