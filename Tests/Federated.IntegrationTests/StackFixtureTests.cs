using Xunit;

namespace Federated.IntegrationTests;

/// <summary>When the tests skip and when they fail. Runs without the dev stack.</summary>
public class StackFixtureTests
{
    private static Task<string?> NoProblem() => Task.FromResult<string?>(null);

    [Fact]
    public async Task Skips_when_Submission_does_not_answer()
    {
        var r = await StackFixture.CheckAsync("localhost", () => throw new HttpRequestException("Connection refused"), NoProblem);

        Assert.False(r.Available);
        Assert.Contains("not running", r.UnavailableReason);
    }

    [Fact]
    public async Task Fails_when_the_stack_runs_but_a_login_is_refused()
    {
        var r = await StackFixture.CheckAsync("localhost", () => Task.CompletedTask,
            () => throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).", null, System.Net.HttpStatusCode.Unauthorized));

        Assert.True(r.Available);
        Assert.Contains("401", r.Problem);
    }

    [Fact]
    public async Task Fails_when_the_stack_runs_but_a_service_is_down()
    {
        var r = await StackFixture.CheckAsync("localhost", () => Task.CompletedTask,
            () => throw new HttpRequestException("Connection refused (localhost:9100)"));

        Assert.True(r.Available);
        Assert.Contains("running but not working", r.Problem);
    }

    [Fact]
    public async Task Reports_the_problem_a_check_names()
    {
        var r = await StackFixture.CheckAsync("localhost", () => Task.CompletedTask,
            () => Task.FromResult<string?>("seed-dataset has not finished"));

        Assert.Equal("seed-dataset has not finished", r.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad Gateway</html>")]
    public void An_error_body_that_is_not_JSON_reads_as_null(string body) =>
        Assert.Null(SubmissionClient.TryParse(body));
}
