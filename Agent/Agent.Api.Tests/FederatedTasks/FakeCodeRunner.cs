using Agent.Api.FederatedTasks;

namespace Agent.Api.Tests.FederatedTasks;

/// <summary>An in-memory container runtime: a run state per task, a scripted result file, and the calls made.</summary>
public sealed class FakeCodeRunner : ICodeRunner
{
    public Dictionary<string, CodeRunState> States { get; } = new();

    public Dictionary<string, DateTimeOffset> CreatedAt { get; } = new();

    public ResultFileRead Result { get; set; } = new(null, false);

    public List<CodeRunRequest> Started { get; } = [];

    /// <summary>The staged files' bytes, by relative path, as they were when <see cref="StartAsync"/> was called.</summary>
    public Dictionary<string, byte[]> StagedBytes { get; } = new();

    public List<string> Stopped { get; } = [];

    public List<string> Removed { get; } = [];

    public List<(string TaskId, int MaxBytes)> Reads { get; } = [];

    /// <summary>Task ids with staged data (volumes or a stage container) but no run, as a removal cut short leaves.</summary>
    public HashSet<string> Orphans { get; } = [];

    /// <summary>Task ids whose removal throws, as Docker does when it cannot remove a volume.</summary>
    public HashSet<string> FailRemove { get; } = [];

    public Task<CodeRunState> GetAsync(string taskId, CancellationToken ct) =>
        Task.FromResult(States.TryGetValue(taskId, out var state) ? state : CodeRunState.None);

    public Task<IReadOnlyList<CodeRunInfo>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<CodeRunInfo> runs = States.Keys
            .Select(id => new CodeRunInfo(id, CreatedAt.TryGetValue(id, out var at) ? at : DateTimeOffset.MinValue))
            .ToList();
        return Task.FromResult(runs);
    }

    public Task<IReadOnlyList<string>> ListOrphanedTaskIdsAsync(CancellationToken ct)
    {
        IReadOnlyList<string> ids = Orphans.Where(id => !States.ContainsKey(id)).ToList();
        return Task.FromResult(ids);
    }

    public async Task StartAsync(CodeRunRequest request, CancellationToken ct)
    {
        Started.Add(request);
        foreach (var file in request.Files)
        {
            using var copy = new MemoryStream();
            await file.Content.CopyToAsync(copy, ct);
            StagedBytes[file.RelativePath] = copy.ToArray();
        }

        States[request.TaskId] = new CodeRunState(CodeRunStatus.Running, DateTimeOffset.UtcNow, null, null, false,
            request.DatasetSha256);
    }

    public Task StopAsync(string taskId, CancellationToken ct)
    {
        Stopped.Add(taskId);
        return Task.CompletedTask;
    }

    public Task<ResultFileRead> ReadResultAsync(string taskId, int maxBytes, CancellationToken ct)
    {
        Reads.Add((taskId, maxBytes));
        return Task.FromResult(Result);
    }

    public Task RemoveAsync(string taskId, CancellationToken ct)
    {
        if (FailRemove.Contains(taskId))
        {
            throw new InvalidOperationException($"cannot remove the run of {taskId}");
        }

        Removed.Add(taskId);
        Orphans.Remove(taskId);
        States.Remove(taskId);
        CreatedAt.Remove(taskId);
        return Task.CompletedTask;
    }
}
