using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;
using Serilog;

namespace Agent.Api
{
    /// <summary>
    /// The Agent in the aggregator role (FederatedTaskSettings:Role = Aggregator). Each poll takes the study versions ready
    /// for aggregation from Submission and moves each one step: check the code's hash and the image, stage the code and
    /// the hospitals' values, start <c>run-analysis --aggregate</c> in the same locked-down container as at a hospital,
    /// then collect its result.json and report the pooled document or a failure code. The container's output is never
    /// read (log driver none); the runner's own message goes to this Agent's log only, as at a hospital.
    /// </summary>
    public class DoFederatedAggregationWork : IDoFederatedTaskWork
    {
        private const string CodeFile = "code/analysis.py";
        private const string ResultsFile = "data/results.json";

        private static readonly string[] AggregateCommand =
            [FederatedTesProfile.RunAnalysisCommand, "--aggregate", FederatedTesProfile.CodePath, "/data/results.json", FederatedTesProfile.ResultPath];

        private readonly IFederatedTaskClient _client;
        private readonly ICodeRunner _runner;
        private readonly FederatedTaskSettings _settings;
        private readonly TimeProvider _clock;

        public DoFederatedAggregationWork(IFederatedTaskClient client, ICodeRunner runner, FederatedTaskSettings settings, TimeProvider clock)
        {
            _client = client;
            _runner = runner;
            _settings = settings;
            _clock = clock;
        }

        /// <summary>The aggregator gets no reviewer decisions.</summary>
        public Task ActOnDecision(string taskId) => Task.CompletedTask;

        public async Task Execute()
        {
            if (!_client.IsConfigured)
            {
                Log.Warning("Federated aggregation: the aggregator's service account is not configured (ConfigInputMethod Upload with ClientId and ClientSecret); not polling");
                return;
            }

            var jobs = await _client.GetPendingAggregationsAsync() ?? [];
            var pending = jobs.Select(j => j.VersionId.ToString("D")).ToHashSet(StringComparer.Ordinal);

            // A run whose version is no longer pending was reported (or the version was retried, timed out or decided
            // otherwise): it is never used again. Staged data without a run is left from a start cut short.
            try
            {
                foreach (var run in await _runner.ListAsync(CancellationToken.None))
                {
                    if (!pending.Contains(run.TaskId))
                    {
                        await _runner.RemoveAsync(run.TaskId, CancellationToken.None);
                    }
                }

                foreach (var id in await _runner.ListOrphanedTaskIdsAsync(CancellationToken.None))
                {
                    await _runner.RemoveAsync(id, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Federated aggregation: old runs could not be removed this poll; they are tried again at the next poll");
            }

            foreach (var job in jobs)
            {
                try
                {
                    await ProcessAsync(job);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Federated aggregation: study version {VersionId} could not be moved on this poll; it is tried again", job.VersionId);
                }
            }
        }

        private async Task ProcessAsync(FederatedAggregationJob job)
        {
            var id = job.VersionId.ToString("D");
            var state = await _runner.GetAsync(id, CancellationToken.None);
            var limit = TimeSpan.FromMinutes(_settings.MaxRunMinutes);
            switch (state.Status)
            {
                case CodeRunStatus.None:
                    await StartAsync(job, id);
                    return;

                case CodeRunStatus.Running:
                    if (state.StartedAt is { } started && _clock.GetUtcNow() - started >= limit)
                    {
                        await _runner.StopAsync(id, CancellationToken.None);
                        Log.Information("Federated aggregation: stopped the run of study version {VersionId} after {Minutes} minutes", id, _settings.MaxRunMinutes);
                        await ReportAndRemoveAsync(job, id, FederatedTaskErrorCodes.Timeout, null);
                    }

                    return;

                case CodeRunStatus.Exited:
                    if (state.StartedAt is { } s && state.FinishedAt is { } f && f - s >= limit)
                    {
                        await ReportAndRemoveAsync(job, id, FederatedTaskErrorCodes.Timeout, null);
                        return;
                    }

                    var (code, pooled) = await CollectAsync(job, id, state);
                    await ReportAndRemoveAsync(job, id, code, pooled);
                    return;

                default:
                    // Created: a start that failed part-way. The next poll starts it again.
                    await _runner.RemoveAsync(id, CancellationToken.None);
                    return;
            }
        }

        private async Task StartAsync(FederatedAggregationJob job, string id)
        {
            var task = JsonConvert.DeserializeObject<TesTask>(job.TesTaskJson);
            var code = task?.Inputs?.FirstOrDefault(i => i.Path == FederatedTesProfile.CodePath)?.Content;
            var sha = code == null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
            if (sha == null || sha != job.CodeSha256 || task!.Tags?.GetValueOrDefault(FederatedTesProfile.CodeSha256Tag) != sha)
            {
                Log.Error("Federated aggregation: the stored code of study version {VersionId} does not match its hash; it is not run", id);
                await _client.ReportAggregationAsync(new FederatedAggregationReport(id, FederatedAggregationPhases.Failed, FederatedTaskErrorCodes.InvalidTask), null);
                return;
            }

            var image = task.Executors?.FirstOrDefault()?.Image;
            if (image == null || !_settings.ApprovedImages.Contains(image, StringComparer.Ordinal))
            {
                Log.Error("Federated aggregation: study version {VersionId} asks for image {Image}, which is not in ApprovedImages; it is not run", id, image);
                await _client.ReportAggregationAsync(new FederatedAggregationReport(id, FederatedAggregationPhases.Failed, FederatedTaskErrorCodes.ImageNotApproved), null);
                return;
            }

            if ((await _runner.ListAsync(CancellationToken.None)).Count >= _settings.MaxConcurrentRuns)
            {
                return;
            }

            var running = await _client.ReportAggregationAsync(new FederatedAggregationReport(id, FederatedAggregationPhases.Running, null), null);
            if (running.StatusCode != 200)
            {
                Log.Warning("Federated aggregation: Submission answered {Status} to running for study version {VersionId}; it is not started", running.StatusCode, id);
                return;
            }

            var (cpu, memory, tmp) = CodeExecution.LimitsFor(id, task.Resources, _settings);
            using var codeStream = new MemoryStream(Encoding.UTF8.GetBytes(code!));
            using var resultsStream = new MemoryStream(Encoding.UTF8.GetBytes(job.ResultsJson));
            await _runner.StartAsync(new CodeRunRequest(id, image, "", null,
                [new StagedFile(CodeFile, codeStream), new StagedFile(ResultsFile, resultsStream)],
                cpu, memory, tmp, AggregateCommand), CancellationToken.None);
            Log.Information("Federated aggregation: started pooling study version {VersionId}", id);
        }

        /// <summary>The failure code, or null with the pooled document.</summary>
        private async Task<(string? Code, byte[]? Pooled)> CollectAsync(FederatedAggregationJob job, string id, CodeRunState state)
        {
            var file = await _runner.ReadResultAsync(id, CodeExecution.MaxResultFileBytes, CancellationToken.None);
            if (file.TooLarge)
            {
                return (FederatedTaskErrorCodes.ResultTooLarge, null);
            }

            if (file.Bytes == null)
            {
                Log.Warning("Federated aggregation: the run of study version {VersionId} ended without result.json (exit code {ExitCode}, out of memory {Oom})",
                    id, state.ExitCode, state.OomKilled);
                return (state.OomKilled ? FederatedTaskErrorCodes.OutOfMemory : FederatedTaskErrorCodes.RunFailed, null);
            }

            JsonElement root;
            try
            {
                using var parsed = JsonDocument.Parse(file.Bytes, new JsonDocumentOptions { MaxDepth = FederatedResultLimits.MaxJsonDepth });
                root = parsed.RootElement.Clone();
            }
            catch (System.Text.Json.JsonException)
            {
                return Invalid(id, "it is not valid JSON");
            }

            var status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : null;
            if (status == "error")
            {
                var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var runnerMessage = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                Log.Warning("Federated aggregation: the run of study version {VersionId} failed with {Code}: {RunnerMessage}", id, code, runnerMessage);
                // The aggregator wrote results.json itself, so a runner that can't read it is the orchestrator's fault.
                return code == "invalid_dataset" ? (FederatedTaskErrorCodes.InternalError, null)
                    : code != null && FederatedAggregationPhases.FailureCodes.Contains(code) && RunErrorMessages.ForRunnerCode(code) != null ? (code, null)
                    : Invalid(id, "it is an error with a code the aggregator does not accept");
            }

            if (status != "ok")
            {
                return Invalid(id, "it has no known status");
            }

            if (!root.TryGetProperty("n", out var nElement) || !nElement.TryGetInt64(out var n) || n < 0
                || !root.TryGetProperty("result", out var value) || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("n", out var valueN) || !valueN.TryGetInt64(out var dictN) || dictN != n)
            {
                return Invalid(id, "n is not a non-negative integer equal to result.n");
            }

            int sites;
            using (var results = JsonDocument.Parse(job.ResultsJson))
            {
                sites = results.RootElement.GetArrayLength();
            }

            var pooled = new FederatedPooledResultDocument
            {
                StudyId = job.StudyId,
                Version = job.Version,
                VersionId = job.VersionId,
                CodeSha256 = job.CodeSha256,
                Sites = sites,
                CompletedAt = _clock.GetUtcNow(),
                Result = new FederatedPooledResult { N = n, Value = value.Clone() }
            }.Serialize();

            // Checked as Submission will, so a document it would refuse fails here with a code instead of being re-sent.
            var version = new FederatedStudyVersion { VersionId = job.VersionId, StudyId = job.StudyId, Version = job.Version, CodeSha256 = job.CodeSha256 };
            if (FederatedPooledResultDocument.Validate(pooled, version, sites) is { } problem)
            {
                return Invalid(id, problem);
            }

            return (null, pooled);
        }

        private static (string, byte[]?) Invalid(string id, string reason)
        {
            Log.Warning("Federated aggregation: the result.json of study version {VersionId} is not accepted: {Reason}", id, reason);
            return (FederatedTaskErrorCodes.InvalidResult, null);
        }

        /// <summary>Reports, then removes the run once Submission took the report or no longer wants one.</summary>
        private async Task ReportAndRemoveAsync(FederatedAggregationJob job, string id, string? failureCode, byte[]? pooled)
        {
            var report = failureCode == null
                ? new FederatedAggregationReport(id, FederatedAggregationPhases.Aggregated, null)
                : new FederatedAggregationReport(id, FederatedAggregationPhases.Failed, failureCode);
            var outcome = await _client.ReportAggregationAsync(report, pooled);
            if (outcome.StatusCode is 200 or 409)
            {
                await _runner.RemoveAsync(id, CancellationToken.None);
                Log.Information("Federated aggregation: study version {VersionId} reported {Phase}{Code}", id, report.Phase,
                    failureCode == null ? "" : " " + failureCode);
            }
            else
            {
                Log.Warning("Federated aggregation: Submission answered {Status} to the report of study version {VersionId}; it is sent again at the next poll",
                    outcome.StatusCode, id);
            }
        }
    }
}
