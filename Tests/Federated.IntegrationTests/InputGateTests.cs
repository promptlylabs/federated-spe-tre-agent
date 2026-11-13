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
        Skip.IfNot(stack.Available, stack.UnavailableReason);
        var queued = await stack.Submission.EnqueueAsync($"missing_{Guid.NewGuid():N}", "count");
        Assert.Equal(200, queued.Status);

        var end = await Await.UntilAsync(stack, queued.Id!, t => t.State != "QUEUED" && t.State != "RUNNING", TimeSpan.FromSeconds(120));

        Assert.Equal("CANCELED", end.State);
        Assert.Equal("rejected_by_input_checks", end.Step);
        Assert.Equal("dataset_not_found", end.ErrorCode);
        Assert.Null(await stack.Submission.GetResultAsync(queued.Id!));
    }

    [SkippableFact]
    public async Task Hospital_input_checks_stop_a_task_whose_dataset_folder_holds_two_files()
    {
        Skip.IfNot(stack.Available, stack.UnavailableReason);
        var dataset = $"ambiguous_{Guid.NewGuid():N}";
        try
        {
            await stack.Storage.PutDatasetFileAsync(dataset, "a.csv", "age,sex\n30,F\n");
            await stack.Storage.PutDatasetFileAsync(dataset, "b.csv", "age,sex\n40,M\n");
            var queued = await stack.Submission.EnqueueAsync(dataset, "count");
            Assert.Equal(200, queued.Status);

            var end = await Await.UntilAsync(stack, queued.Id!, t => t.State is not ("QUEUED" or "RUNNING"), TimeSpan.FromSeconds(120));

            Assert.Equal("CANCELED", end.State);
            Assert.Equal("rejected_by_input_checks", end.Step);
            Assert.Equal("ambiguous_dataset", end.ErrorCode);
            Assert.Null(await stack.Submission.GetResultAsync(queued.Id!));
        }
        finally
        {
            await stack.Storage.DeleteDatasetAsync(dataset);
        }
    }

    [SkippableTheory]
    [InlineData("unknown operation", "unknown_operation")]
    [InlineData("invalid filter", "invalid_filter")]
    [InlineData("foreign image", "invalid_task")]
    public async Task Submission_refuses_a_task_outside_the_federated_profile(string kind, string expectedCode)
    {
        Skip.IfNot(stack.Available, stack.UnavailableReason);
        var outcome = kind switch
        {
            "unknown operation" => await stack.Submission.EnqueueAsync("cohort_2024", "median"),
            "invalid filter" => await stack.Submission.EnqueueAsync("cohort_2024", "count", "--filter", "sex:like:F"),
            _ => await stack.Submission.EnqueueWithImageAsync("python:3.12", "cohort_2024", "count")
        };

        Assert.Equal(400, outcome.Status);
        Assert.Equal(expectedCode, outcome.ErrorCode);
        Assert.Null(outcome.Id);
    }
}
