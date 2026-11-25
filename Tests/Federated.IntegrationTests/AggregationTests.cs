using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Federated.IntegrationTests;

/// <summary>
/// Pooling a study's results (ANZE-6132). my-tre's Agent runs and its reviewer approves; the test stands in for
/// other-tre's Agent and releases a chosen result; then the sweep marks the version ready and the aggregator runs the
/// researcher's aggregate(). The no-network case runs as an Agent Docker test: the hospitals' code scan refuses socket.
/// </summary>
[Collection(StackCollection.Name)]
public class AggregationTests
{
    /// <summary>The sweep (15 s), an aggregator poll (1 min) and a run, which is slow under emulation on Apple silicon.</summary>
    private static readonly TimeSpan PoolWait = TimeSpan.FromMinutes(6);

    private readonly StackFixture stack;

    public AggregationTests(StackFixture stack) => this.stack = stack;

    /// <summary>Enqueues the code for both hospitals, releases other-tre's <paramref name="otherValue"/> and my-tre's own result, and waits for the outcome.</summary>
    private async Task<(EnqueueOutcome Version, StudyView Ended)> PoolAsync(string code, JsonObject otherValue)
    {
        var studyId = $"study-{Guid.NewGuid():N}";
        var version = await stack.Submission.EnqueueCodeAsync("cohort_2024", code, sites: StackSettings.BothHospitals, studyId: studyId);
        Assert.Equal(200, version.Status);
        var otherTaskId = (await stack.Submission.GetStudyVersionAsync(version.VersionId!)).Hospitals.Single(h => h.Tre == StackSettings.OtherTre).TaskId;
        await stack.OtherTre.ReleaseAsync(otherTaskId, studyId, otherValue);

        var waiting = await Await.UntilSettledAsync(stack, version.Id!, Await.PhaseWait);
        Assert.Equal("waiting_for_output_approval", waiting.Phase);
        await stack.Egress.DecideAsync(version.Id!, approve: true, reason: null);
        var released = await Await.UntilAsync(stack, version.Id!, t => t.Phase != "waiting_for_output_approval", Await.PhaseWait);
        Assert.Equal("released", released.Phase);

        var ended = await Await.UntilStudyAsync(stack, version.VersionId!, s => s.Outcome is "aggregated" or "failed", PoolWait);
        return (version, ended);
    }

    [SkippableFact]
    public async Task Two_hospitals_are_pooled()
    {
        stack.EnsureReady();

        var (version, ended) = await PoolAsync(Code.MeanAge, new JsonObject { ["n"] = 2, ["mean_age"] = 30.0 });

        Assert.Equal("aggregated", ended.Outcome);
        var mine = JsonDocument.Parse((await stack.Submission.GetResultAsync(version.Id!))!.Bytes).RootElement.GetProperty("result").GetProperty("value");
        var (myN, myMean) = (mine.GetProperty("n").GetInt32(), mine.GetProperty("mean_age").GetDouble());
        var pooledBytes = await stack.Results.GetPooledAsync(version.VersionId!);
        Assert.NotNull(pooledBytes);
        Assert.Equal(ended.PooledResultSha256, SubmissionClient.CodeSha256Bytes(pooledBytes));
        var pooled = JsonDocument.Parse(pooledBytes).RootElement;
        Assert.Equal(2, pooled.GetProperty("sites").GetInt32());
        var value = pooled.GetProperty("result").GetProperty("value");
        Assert.Equal(myN + 2, value.GetProperty("n").GetInt32());
        Assert.Equal((myN * myMean + 2 * 30.0) / (myN + 2), value.GetProperty("mean_age").GetDouble(), 9);

        // 3.4: the pooled result read back, hash checked, and the audit trail of how it was produced.
        var (status, served) = await stack.Submission.GetStudyResultAsync(version.VersionId!);
        Assert.Equal(200, status);
        Assert.Equal(pooledBytes, served);
        var audit = await stack.Submission.GetStudyAuditAsync(version.VersionId!);
        Assert.Equal("control-admin", audit["submitted_by"]!.GetValue<string>());
        Assert.Equal(ended.PooledResultSha256, audit["pooled_result_sha256"]!.GetValue<string>());
        Assert.NotNull(audit["aggregation_started_at"]?.GetValue<string>());
        var hospitals = audit["hospitals"]!.AsArray();
        Assert.Equal(new[] { StackSettings.Tre, StackSettings.OtherTre }, hospitals.Select(h => h!["tre"]!.GetValue<string>()));
        Assert.All(hospitals, h =>
        {
            Assert.Matches("^[0-9a-f]{64}$", h!["result_sha256"]!.GetValue<string>());
            Assert.Equal("released", h["phases"]!.AsArray().Last()!["phase"]!.GetValue<string>());
        });
    }

    [SkippableFact]
    public async Task Aggregate_that_raises_fails_the_study()
    {
        stack.EnsureReady();

        var (version, ended) = await PoolAsync(Code.AggregateRaises, new JsonObject { ["n"] = 2 });

        Assert.Equal(("failed", "code_error"), (ended.Outcome, ended.AggregationErrorCode));
        // The line of aggregate() that raised (line 6 of Code.AggregateRaises), and nothing of the exception itself.
        Assert.Equal("aggregate() could not be imported, is missing, or raised an exception (analysis.py line 6)", ended.OutcomeMessage);
        Assert.Null(ended.PooledResultSha256);
        Assert.Null(await stack.Results.GetPooledAsync(version.VersionId!));
    }

    [SkippableFact]
    public async Task Code_without_aggregate_is_refused_at_intake()
    {
        stack.EnsureReady();

        var outcome = await stack.Submission.EnqueueCodeAsync("cohort_2024", Code.NoAggregate);

        Assert.Equal((400, "invalid_task"), (outcome.Status, outcome.ErrorCode));
    }
}
