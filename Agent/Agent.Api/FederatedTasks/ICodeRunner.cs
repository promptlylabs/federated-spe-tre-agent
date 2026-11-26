namespace Agent.Api.FederatedTasks
{
    /// <summary>Where a task's run is: none, created but not started (a start that failed part-way), running or exited.</summary>
    public enum CodeRunStatus { None, Created, Running, Exited }

    /// <summary>
    /// A task's run as the container runtime sees it. <see cref="DatasetSha256"/> is the hash of the CSV bytes that were
    /// staged, kept with the run so a later poll (or an Agent restart) checks the result against the data that ran.
    /// </summary>
    public sealed record CodeRunState(
        CodeRunStatus Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        long? ExitCode,
        bool OomKilled,
        string? DatasetSha256)
    {
        public static readonly CodeRunState None = new(CodeRunStatus.None, null, null, null, false, null);
    }

    /// <summary>A run the runtime holds, as listed for the sweep and the limit on runs at the same time.</summary>
    public sealed record CodeRunInfo(string TaskId, DateTimeOffset CreatedAt);

    /// <summary>A file to stage, relative to the data volume root, e.g. "data/cohort_2024/cohort.csv", "code/analysis.py".</summary>
    public sealed record StagedFile(string RelativePath, Stream Content);

    /// <summary>What to stage and run for a task. The runner builds the command itself; the task's command is never used.</summary>
    public sealed record CodeRunRequest(
        string TaskId,
        string Image,
        string DatasetName,
        string DatasetSha256,
        IReadOnlyList<StagedFile> Files,
        double CpuCores,
        long MemoryBytes,
        long TmpBytes);

    /// <summary>The run's result.json: <see cref="Bytes"/> null and <see cref="TooLarge"/> false means there is no file.</summary>
    public sealed record ResultFileRead(byte[]? Bytes, bool TooLarge);

    /// <summary>
    /// The container runtime that runs researcher code. Every member is keyed by the canonical task id; the runs
    /// themselves are the record of where each task is, so nothing about them is kept in the Agent.
    /// </summary>
    public interface ICodeRunner
    {
        Task<CodeRunState> GetAsync(string taskId, CancellationToken ct);

        Task<IReadOnlyList<CodeRunInfo>> ListAsync(CancellationToken ct);

        /// <summary>
        /// The task ids that have a volume or container labelled with a task id but no run container: what a removal
        /// or failed start cut short leaves behind, such as a copy of the dataset in a volume.
        /// </summary>
        Task<IReadOnlyList<string>> ListOrphanedTaskIdsAsync(CancellationToken ct);

        /// <summary>Stages the files, then starts the run. Removes any earlier run for the task first.</summary>
        Task StartAsync(CodeRunRequest request, CancellationToken ct);

        Task StopAsync(string taskId, CancellationToken ct);

        /// <summary>Reads at most <paramref name="maxBytes"/> of the run's result.json.</summary>
        Task<ResultFileRead> ReadResultAsync(string taskId, int maxBytes, CancellationToken ct);

        /// <summary>Removes the run's container(s) and both volumes. Safe to repeat.</summary>
        Task RemoveAsync(string taskId, CancellationToken ct);
    }
}
