using Xunit;

namespace Federated.IntegrationTests;

[CollectionDefinition(Name)]
public class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}

/// <summary>The running dev stack. When it is not reachable, Available is false and every test skips.</summary>
public sealed class StackFixture : IAsyncLifetime
{
    private const string StartCommand = "docker compose -f dev/federated/docker-compose.yml up -d --build";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public StackFixture()
    {
        var settings = new StackSettings();
        Submission = new SubmissionClient(http, settings);
        Egress = new EgressClient(http, settings);
        Agent = new AgentPoll(settings);
        Storage = new TreStorage(settings);
        Host = settings.Host;
    }

    public string Host { get; }
    public bool Available { get; private set; }
    public string? UnavailableReason { get; private set; }
    public SubmissionClient Submission { get; }
    public EgressClient Egress { get; }
    public AgentPoll Agent { get; }
    public TreStorage Storage { get; }

    public async Task InitializeAsync()
    {
        try
        {
            // Each call logs in and reaches one service, so a missing service or seed step shows up here.
            using (var r = await Submission.SendAsync(HttpMethod.Get, $"/api/FederatedTask/GetTask?id={Guid.NewGuid()}")) { }
            using (var r = await Egress.SendAsync(HttpMethod.Get, "/api/DataEgress/GetAllEgresses?unprocessedonly=true"))
                r.EnsureSuccessStatusCode();
            await Storage.ListDatasetsAsync();
            Available = true;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"The dev stack at {Host} is not reachable ({ex.GetType().Name}: {ex.Message}). Start it with: {StartCommand}" +
                                (Host == "localhost" ? " (from a container, set FEDERATED_STACK_HOST=host.docker.internal)" : "");
            return;
        }

        // Reachable but not seeded: the tests fail (not skip) and say which seed step is missing.
        if (!await Storage.HasDatasetAsync("cohort_2024"))
            SeedProblem = "The dev stack has no dataset cohort_2024: the seed step seed-dataset has not finished. Run: docker compose -f dev/federated/docker-compose.yml up seed-dataset";
        else
        {
            using var creds = await Egress.SendAsync(HttpMethod.Get, "/api/TreCredentials/CheckCredentialsAreValid");
            if (!creds.IsSuccessStatusCode || !(await creds.Content.ReadAsStringAsync()).Contains("\"result\":true", StringComparison.OrdinalIgnoreCase))
                SeedProblem = "The Egress app has no usable TRE login, so decisions cannot reach the Agent: the seed step seed-egress-creds has not finished. Run: docker compose -f dev/federated/docker-compose.yml up seed-egress-creds";
        }
    }

    /// <summary>Skips the test when the stack is not reachable; fails it, naming the step, when it is not seeded.</summary>
    public void EnsureReady()
    {
        Skip.IfNot(Available, UnavailableReason);
        if (SeedProblem != null) Assert.Fail(SeedProblem);
    }

    public string? SeedProblem { get; private set; }



    public Task DisposeAsync()
    {
        http.Dispose();
        Storage.Dispose();
        return Task.CompletedTask;
    }
}
