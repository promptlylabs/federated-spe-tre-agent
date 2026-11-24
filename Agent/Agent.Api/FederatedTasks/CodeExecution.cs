using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Serilog;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Runs a code task's researcher file in a container through <see cref="ICodeRunner"/>. Each call moves the run one
    /// step: stage and start it, check it, or map its result.json. Nothing here waits for the run.
    /// </summary>
    /// <remarks>
    /// Every failure of a run is <see cref="ExecutionResult.RunFailed"/> with a fixed message, so nothing the code wrote
    /// reaches the orchestrator except through output approval. A final outcome leaves the run in place: the poll removes
    /// it once the report is settled or the result is saved, and a poll that lost its report gets the same outcome again.
    /// </remarks>
    public sealed class CodeExecution : IFederatedTaskExecution
    {
        /// <summary>The most of result.json the Agent reads; more is result_too_large.</summary>
        public const int MaxResultFileBytes = 65_536;

        private const string SchemaFileName = "schema.json";
        private const long BytesPerGiB = 1024L * 1024 * 1024;

        // Floors for a tiny request: Docker reads a memory, CPU or tmpfs size of 0 as no limit at all.
        private const double MinCpuCores = 0.01;
        private const double MinMemoryBytes = 256 * 1024 * 1024;
        private const double MinTmpBytes = 16 * 1024 * 1024;

        private readonly ICodeRunner _runner;
        private readonly IDatasetStore _store;
        private readonly IResultOutputStore _outputs;
        private readonly FederatedTaskSettings _settings;
        private readonly TimeProvider _clock;

        public CodeExecution(ICodeRunner runner, IDatasetStore store, IResultOutputStore outputs,
            FederatedTaskSettings settings, TimeProvider clock)
        {
            _runner = runner;
            _store = store;
            _outputs = outputs;
            _settings = settings;
            _clock = clock;
        }

        public async Task<bool> HasRunAsync(string taskId, CancellationToken ct) =>
            (await _runner.GetAsync(taskId, ct)).Status != CodeRunStatus.None;

        public async Task<bool> CanStartAsync(FederatedTaskSpec spec, CancellationToken ct) =>
            (await _runner.ListAsync(ct)).Count < _settings.MaxConcurrentRuns;

        public Task RemoveRunAsync(string taskId, CancellationToken ct) => _runner.RemoveAsync(taskId, ct);

        public async Task SweepAsync(IReadOnlyCollection<string> pendingTaskIds, CancellationToken ct)
        {
            var now = _clock.GetUtcNow();
            var staleAfter = TimeSpan.FromMinutes(2 * _settings.MaxRunMinutes);
            foreach (var run in await _runner.ListAsync(ct))
            {
                // A saved result means the task is never run again. Past twice the limit, a poll that saw the task
                // would have timed it out; the pending list is capped, so being absent from it alone proves nothing.
                string? reason = null;
                if (await _outputs.TryGetAsync(run.TaskId, ct) != null)
                {
                    reason = "its result is saved";
                }
                else if (!pendingTaskIds.Contains(run.TaskId) && now - run.CreatedAt > staleAfter)
                {
                    reason = $"it is not pending and is older than {2 * _settings.MaxRunMinutes} minutes";
                }

                if (reason == null)
                {
                    continue;
                }

                await _runner.RemoveAsync(run.TaskId, ct);
                Log.Information("Federated tasks: removed the code run of task {TaskId}: {Reason}", run.TaskId, reason);
            }
        }

        public async Task<ExecutionResult> RunAsync(TesTask task, FederatedTaskSpec spec, string? fileKey, CancellationToken ct)
        {
            var taskId = Guid.Parse(task.Id!).ToString("D");
            return fileKey != null
                ? await StartAsync(task, spec, taskId, fileKey, ct)
                : await CheckAsync(spec, taskId, ct);
        }

        private async Task<ExecutionResult> StartAsync(TesTask task, FederatedTaskSpec spec, string taskId, string fileKey,
            CancellationToken ct)
        {
            var folder = fileKey[..(fileKey.LastIndexOf('/') + 1)];
            var csvName = fileKey[folder.Length..];
            var schemaKey = folder + SchemaFileName;

            await using var csv = await OpenAsync(fileKey, ct);
            await using var schema = await OpenAsync(schemaKey, ct);
            string sha256;
            try
            {
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(csv, ct));
                csv.Position = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DatasetStoreException($"Reading dataset file '{fileKey}' failed", ex);
            }

            // The profile guarantees exactly one code input with content.
            var code = task.Inputs!.Single(i => i.Path == FederatedTesProfile.CodePath).Content!;
            using var codeStream = new MemoryStream(Encoding.UTF8.GetBytes(code));

            var dataFolder = $"data/{spec.Dataset}/";
            var resources = task.Resources;
            var request = new CodeRunRequest(
                taskId,
                task.Executors![0].Image,
                spec.Dataset,
                sha256,
                [
                    new StagedFile(dataFolder + csvName, csv),
                    new StagedFile(dataFolder + SchemaFileName, schema),
                    new StagedFile("code/analysis.py", codeStream)
                ],
                AtLeast(taskId, "cpu_cores",
                    Limit(taskId, "cpu_cores", resources?.CpuCores, _settings.DefaultCpuCores, _settings.MaxCpuCores),
                    MinCpuCores),
                (long)AtLeast(taskId, "ram_gb bytes",
                    Limit(taskId, "ram_gb", resources?.RamGb, _settings.DefaultRamGb, _settings.MaxRamGb) * BytesPerGiB,
                    MinMemoryBytes),
                (long)AtLeast(taskId, "disk_gb bytes",
                    Limit(taskId, "disk_gb", resources?.DiskGb, _settings.DefaultDiskGb, _settings.MaxDiskGb) * BytesPerGiB,
                    MinTmpBytes));

            await _runner.StartAsync(request, ct);
            return ExecutionResult.InProgress();
        }

        private async Task<Stream> OpenAsync(string key, CancellationToken ct)
        {
            try
            {
                return await _store.OpenReadAsync(key, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DatasetStoreException($"Reading dataset file '{key}' failed", ex);
            }
        }

        /// <summary>The task's value, or the default when it gives none; never more than the TRE's maximum.</summary>
        /// <summary>The value, raised to the floor when it is below it.</summary>
        private static double AtLeast(string taskId, string field, double value, double floor)
        {
            if (value >= floor)
            {
                return value;
            }

            Log.Information("Federated tasks: task {TaskId} gets {Field} {Floor}, the least a run gets, instead of {Value}",
                taskId, field, floor, value);
            return floor;
        }

        private static double Limit(string taskId, string field, double? requested, double fallback, double max)
        {
            if (requested is not { } value || !double.IsFinite(value) || value <= 0)
            {
                return Math.Min(fallback, max);
            }

            if (value > max)
            {
                Log.Information("Federated tasks: task {TaskId} asked for {Field} {Requested}; the TRE's maximum {Max} is used",
                    taskId, field, value, max);
                return max;
            }

            return value;
        }

        private async Task<ExecutionResult> CheckAsync(FederatedTaskSpec spec, string taskId, CancellationToken ct)
        {
            var state = await _runner.GetAsync(taskId, ct);
            var limit = TimeSpan.FromMinutes(_settings.MaxRunMinutes);
            var timeout = ExecutionResult.RunFailed(FederatedTaskErrorCodes.Timeout,
                RunErrorMessages.Timeout(_settings.MaxRunMinutes), state.DatasetSha256);

            switch (state.Status)
            {
                case CodeRunStatus.Running:
                    var startedAt = state.StartedAt ?? _clock.GetUtcNow();
                    if (_clock.GetUtcNow() - startedAt < limit)
                    {
                        return ExecutionResult.InProgress();
                    }

                    await _runner.StopAsync(taskId, ct);
                    Log.Information("Federated tasks: stopped the code run of task {TaskId} after {Minutes} minutes",
                        taskId, _settings.MaxRunMinutes);
                    return timeout;

                case CodeRunStatus.Exited:
                    // Stopped for the time limit by an earlier poll whose report did not settle.
                    if (state.StartedAt is { } started && state.FinishedAt is { } finished && finished - started >= limit)
                    {
                        return timeout;
                    }

                    return await CollectAsync(spec, taskId, state, ct);

                default:
                    // Created: a start that failed part-way. Without a run, the next poll checks and starts the task again.
                    await _runner.RemoveAsync(taskId, ct);
                    Log.Information("Federated tasks: removed the code run of task {TaskId}, which was {Status}; it is started again",
                        taskId, state.Status);
                    return ExecutionResult.InProgress();
            }
        }

        private async Task<ExecutionResult> CollectAsync(FederatedTaskSpec spec, string taskId, CodeRunState state,
            CancellationToken ct)
        {
            var sha256 = state.DatasetSha256;
            var file = await _runner.ReadResultAsync(taskId, MaxResultFileBytes, ct);
            if (file.TooLarge)
            {
                return ExecutionResult.RunFailed(FederatedTaskErrorCodes.ResultTooLarge, RunErrorMessages.InvalidResultFile, sha256);
            }

            if (file.Bytes == null)
            {
                Log.Warning("Federated tasks: the code run of task {TaskId} ended without result.json (exit code {ExitCode}, out of memory {Oom})",
                    taskId, state.ExitCode, state.OomKilled);
                return state.OomKilled
                    ? ExecutionResult.RunFailed(FederatedTaskErrorCodes.OutOfMemory, RunErrorMessages.OutOfMemory, sha256)
                    : ExecutionResult.RunFailed(FederatedTaskErrorCodes.RunFailed, RunErrorMessages.RunFailed, sha256);
            }

            return Map(spec, taskId, file.Bytes, sha256);
        }

        private ExecutionResult Map(FederatedTaskSpec spec, string taskId, byte[] bytes, string? sha256)
        {
            var invalid = ExecutionResult.RunFailed(FederatedTaskErrorCodes.InvalidResult, RunErrorMessages.InvalidResultFile, sha256);

            JsonElement root;
            try
            {
                using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = FederatedResultLimits.MaxJsonDepth,
                    AllowDuplicateProperties = false
                });
                root = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Invalid(taskId, "it is not valid JSON", invalid);
            }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
            {
                return Invalid(taskId, "it has no status", invalid);
            }

            switch (status.GetString())
            {
                case "error":
                    if (root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                        && codeElement.GetString() is { } code && RunErrorMessages.ForRunnerCode(code) is { } message)
                    {
                        // The runner's own message can hold the researcher's text: the Agent log only.
                        var runnerMessage = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString()
                            : null;
                        Log.Warning("Federated tasks: the code run of task {TaskId} failed with {Code}: {RunnerMessage}",
                            taskId, code, runnerMessage);
                        return ExecutionResult.RunFailed(code, message, sha256);
                    }

                    return Invalid(taskId, "it is an error with an unknown code", invalid);

                case "ok":
                    return MapOk(spec, taskId, root, sha256, invalid);

                default:
                    return Invalid(taskId, "its status is unknown", invalid);
            }
        }

        private ExecutionResult MapOk(FederatedTaskSpec spec, string taskId, JsonElement root, string? sha256,
            ExecutionResult invalid)
        {
            if (!root.TryGetProperty("n", out var nElement) || nElement.ValueKind != JsonValueKind.Number
                || !nElement.TryGetInt64(out var n) || n < 0)
            {
                return Invalid(taskId, "n is not a non-negative integer", invalid);
            }

            if (!root.TryGetProperty("result", out var value) || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("n", out var valueN) || valueN.ValueKind != JsonValueKind.Number
                || !valueN.TryGetInt64(out var dictN) || dictN != n)
            {
                return Invalid(taskId, "result is not an object whose n equals n", invalid);
            }

            if (sha256 == null || !root.TryGetProperty("datasetSha256", out var runnerSha)
                || runnerSha.ValueKind != JsonValueKind.String || runnerSha.GetString() != sha256)
            {
                return Invalid(taskId, "its datasetSha256 is not the hash of the staged dataset", invalid);
            }

            var doc = new FederatedAnalysisResultDocument
            {
                TaskId = taskId,
                Tre = spec.TreName,
                StudyId = spec.StudyId,
                Operation = spec.Operation,
                Dataset = spec.Dataset,
                DatasetSha256 = sha256,
                CompletedAt = _clock.GetUtcNow(),
                Result = new FederatedAnalysisResult { Kind = "analysis", N = n, Value = value.Clone() }
            };

            // Check the document as Submission will, so a result it would refuse fails now and not after approval.
            var serialized = FederatedResultDocumentJson.SerializeAnalysis(doc);
            if (serialized.Length > FederatedResultLimits.MaxResultBytes)
            {
                return Invalid(taskId, "the result document is larger than the TRE accepts", invalid);
            }

            var row = new FederatedTask
            {
                TaskId = Guid.Parse(taskId),
                TreName = spec.TreName,
                StudyId = spec.StudyId,
                Operation = spec.Operation,
                Dataset = spec.Dataset
            };
            var validation = FederatedResultDocumentJson.Validate(serialized, row);
            if (!validation.IsValid)
            {
                return Invalid(taskId, $"the result document is not valid: {validation.Message}", invalid);
            }

            return ExecutionResult.Ok(doc);
        }

        private static ExecutionResult Invalid(string taskId, string reason, ExecutionResult invalid)
        {
            Log.Warning("Federated tasks: the result.json of task {TaskId} is not accepted: {Reason}", taskId, reason);
            return invalid;
        }
    }
}
