using System.Text.Json.Nodes;
using Xunit;

namespace Federated.IntegrationTests;

[CollectionDefinition(Name)]
public class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}

/// <summary>What the fixture found: the stack is not running (skip), or it runs but something is wrong (fail).</summary>
public record StackReadiness(bool Available, string? UnavailableReason, string? Problem);

/// <summary>
/// The running dev stack. When it is not running, every test skips. When it runs but a service, login or seed step
/// is broken, every test fails and says what is wrong.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    private const string Compose = "docker compose -f dev/federated/docker-compose.yml";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly StackSettings settings = new();

    public StackFixture()
    {
        Submission = new SubmissionClient(http, settings);
        Egress = new EgressClient(http, settings);
        Agent = new AgentPoll(settings);
        Storage = new TreStorage(settings);
        Results = new ResultStore(settings);
        Host = settings.Host;
    }

    public string Host { get; }
    public bool Available { get; private set; }
    public string? UnavailableReason { get; private set; }
    public string? Problem { get; private set; }
    public SubmissionClient Submission { get; }
    public EgressClient Egress { get; }
    public AgentPoll Agent { get; }
    public TreStorage Storage { get; }
    public ResultStore Results { get; }

    public async Task InitializeAsync()
    {
        var readiness = await CheckAsync(Host, SubmissionAnswersAsync, FindProblemAsync);
        (Available, UnavailableReason, Problem) = (readiness.Available, readiness.UnavailableReason, readiness.Problem);
    }

    /// <summary>
    /// Skip only when Submission does not answer at all: the stack is not running. Once it answers, any failure is a
    /// broken stack (a login refused, a service down, a seed step not finished) and must fail the tests, not hide them.
    /// </summary>
    internal static async Task<StackReadiness> CheckAsync(string host, Func<Task> submissionAnswers, Func<Task<string?>> findProblem)
    {
        try
        {
            await submissionAnswers();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new StackReadiness(false,
                $"The dev stack at {host} is not running ({ex.GetType().Name}: {ex.Message}). Start it with: {Compose} up -d --build" +
                (host == "localhost" ? " (from a container, set FEDERATED_STACK_HOST=host.docker.internal)" : ""), null);
        }

        try
        {
            return new StackReadiness(true, null, await findProblem());
        }
        catch (Exception ex)
        {
            return new StackReadiness(true, null,
                $"The dev stack at {host} is running but not working: {ex.GetType().Name}: {ex.Message}. Check: {Compose} ps");
        }
    }

    private async Task SubmissionAnswersAsync()
    {
        using var response = await http.GetAsync(new Uri(settings.Submission, "/health"));
    }

    /// <summary>A description of what is broken, or null when every service, login and seed step is ready.</summary>
    private async Task<string?> FindProblemAsync()
    {
        using (var r = await Submission.SendAsync(HttpMethod.Get, $"/api/FederatedTask/GetTask?id={Guid.NewGuid()}"))
            if ((int)r.StatusCode != 404)
                return $"Submission's GetTask answered HTTP {(int)r.StatusCode} for an unknown task (expected 404). Check: {Compose} logs submission-api";

        try
        {
            using var agent = await http.GetAsync(settings.Agent);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return $"The Agent at {settings.Agent} does not answer ({ex.Message}), so no task would be picked up. Check: {Compose} ps agent-api";
        }

        using (var r = await Egress.SendAsync(HttpMethod.Get, "/api/DataEgress/GetAllEgresses?unprocessedonly=true"))
            if (!r.IsSuccessStatusCode)
                return $"The Egress API answered HTTP {(int)r.StatusCode} to the reviewer. Check: {Compose} logs egress-api";

        await Storage.ListDatasetsAsync();
        await Results.ListAsync();

        if (!await Storage.HasDatasetAsync("cohort_2024"))
            return $"The dev stack has no dataset cohort_2024: the seed step seed-dataset has not finished. Run: {Compose} up seed-dataset";

        using var creds = await Egress.SendAsync(HttpMethod.Get, "/api/TreCredentials/CheckCredentialsAreValid");
        var valid = creds.IsSuccessStatusCode &&
                    SubmissionClient.TryParse(await creds.Content.ReadAsStringAsync())?["result"] is JsonValue v &&
                    v.TryGetValue(out bool ok) && ok;
        return valid
            ? null
            : $"The Egress app has no usable TRE login, so decisions cannot reach the Agent: the seed step seed-egress-creds has not finished. Run: {Compose} up seed-egress-creds";
    }

    /// <summary>Skips the test when the stack is not running; fails it, saying what is wrong, when it is broken.</summary>
    public void EnsureReady()
    {
        Skip.IfNot(Available, UnavailableReason);
        if (Problem != null) Assert.Fail(Problem);
    }

    public Task DisposeAsync()
    {
        http.Dispose();
        Storage.Dispose();
        Results.Dispose();
        return Task.CompletedTask;
    }
}
