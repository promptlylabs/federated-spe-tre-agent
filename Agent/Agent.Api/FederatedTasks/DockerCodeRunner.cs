using System.Formats.Tar;
using System.Globalization;
using System.Net;
using Agent.Api.Models;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Runs researcher code in a locked-down Docker container. A run is three volumes and a container, all named and
    /// labelled with the task id: the dataset (read-only at /data), the code (read-only at /code) and the outputs
    /// (read-write at /outputs). The container is the record of the run; nothing about it is kept in the Agent.
    /// </summary>
    /// <remarks>
    /// The container's stdout and stderr are never read and Docker does not keep them (log driver none): the code can
    /// print data values. The image is never pulled: a missing image fails the start.
    /// </remarks>
    public sealed class DockerCodeRunner : ICodeRunner
    {
        private const string TaskIdLabel = "fed.task-id";
        private const string RoleLabel = "fed.role";
        private const string DatasetShaLabel = "fed.dataset-sha256";
        private const string RunRole = "run";
        private const string ResultPath = "/outputs/result.json";

        /// <summary>The image's user (1001) in group 0, as OpenShift runs it.</summary>
        private const string RunUser = "1001:0";

        private const long PidsLimit = 256;
        private const long MinMemoryBytes = 6 * 1024 * 1024;
        private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        private const UnixFileMode DirectoryMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
                                                   UnixFileMode.OtherExecute;

        private readonly IDockerClient _docker;

        public DockerCodeRunner(IDockerClient docker, FederatedTaskSettings settings)
        {
            _docker = docker;
        }

        private static string RunName(string taskId) => $"fed-run-{taskId}";
        private static string StageName(string taskId) => $"fed-stage-{taskId}";
        private static string DataVolume(string taskId) => $"fed-data-{taskId}";
        private static string CodeVolume(string taskId) => $"fed-code-{taskId}";
        private static string OutputsVolume(string taskId) => $"fed-out-{taskId}";

        private static Dictionary<string, string> Labels(string taskId, string role) =>
            new() { [TaskIdLabel] = taskId, [RoleLabel] = role };

        public async Task<CodeRunState> GetAsync(string taskId, CancellationToken ct)
        {
            ContainerInspectResponse container;
            try
            {
                container = await _docker.Containers.InspectContainerAsync(RunName(taskId), ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return CodeRunState.None;
            }

            var state = container.State;
            var status = state.Status switch
            {
                "running" or "paused" or "restarting" => CodeRunStatus.Running,
                "exited" or "dead" => CodeRunStatus.Exited,
                _ => CodeRunStatus.Created
            };
            var startedAt = ParseDockerTime(state.StartedAt);
            if (status is CodeRunStatus.Running or CodeRunStatus.Exited && startedAt == null)
            {
                // Docker always records a start for these; the creation time keeps the time limit working if not.
                startedAt = new DateTimeOffset(DateTime.SpecifyKind(container.Created, DateTimeKind.Utc));
            }

            container.Config.Labels.TryGetValue(DatasetShaLabel, out var datasetSha256);
            return new CodeRunState(
                status,
                status == CodeRunStatus.Created ? null : startedAt,
                status == CodeRunStatus.Exited ? ParseDockerTime(state.FinishedAt) : null,
                status == CodeRunStatus.Exited ? state.ExitCode : null,
                state.OOMKilled,
                datasetSha256);
        }

        public async Task<IReadOnlyList<CodeRunInfo>> ListAsync(CancellationToken ct)
        {
            var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = LabelFilter($"{RoleLabel}={RunRole}")
            }, ct);
            return containers
                .Where(c => c.Labels.ContainsKey(TaskIdLabel))
                .Select(c => new CodeRunInfo(c.Labels[TaskIdLabel],
                    new DateTimeOffset(DateTime.SpecifyKind(c.Created, DateTimeKind.Utc))))
                .ToList();
        }

        public async Task StartAsync(CodeRunRequest request, CancellationToken ct)
        {
            CheckLimits(request);
            var taskId = request.TaskId;
            await RemoveAsync(taskId, ct);
            try
            {
                foreach (var (name, role) in new[]
                         {
                             (DataVolume(taskId), "data"), (CodeVolume(taskId), "code"), (OutputsVolume(taskId), "outputs")
                         })
                {
                    await _docker.Volumes.CreateAsync(new VolumesCreateParameters
                    {
                        Name = name,
                        Labels = Labels(taskId, role)
                    }, ct);
                }

                await StageAsync(request, ct);
                await CreateRunAsync(request, ct);
                await _docker.Containers.StartContainerAsync(RunName(taskId), new ContainerStartParameters(), ct);
            }
            catch
            {
                // A start that fails part-way leaves nothing behind; the next poll starts the task again. If Docker
                // cannot remove it either, the start's own error is the one reported, and the next start removes it.
                try
                {
                    await RemoveAsync(taskId, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    Serilog.Log.Warning(cleanup, "Federated tasks: could not remove the failed start of task {TaskId}", taskId);
                }

                throw;
            }
        }

        /// <summary>
        /// Docker reads a memory, CPU or tmpfs size of 0 as no limit, so a run never starts with one. Checked before
        /// anything is created. Docker's own least memory is 6 MiB.
        /// </summary>
        private static void CheckLimits(CodeRunRequest request)
        {
            if (request.MemoryBytes < MinMemoryBytes || request.TmpBytes <= 0 || NanoCpus(request.CpuCores) <= 0)
            {
                throw new ArgumentException(
                    $"Run limits for task {request.TaskId} would not limit the run: memory {request.MemoryBytes} bytes, " +
                    $"/tmp {request.TmpBytes} bytes, {request.CpuCores} CPU cores");
            }
        }

        private static long NanoCpus(double cores) => double.IsFinite(cores) ? (long)(cores * 1e9) : 0;

        /// <summary>
        /// Fills the data and code volumes through a container that is created but never started. Mounting the outputs
        /// volume at /outputs here makes Docker copy the image's /outputs ownership (user 1001) into the new volume.
        /// </summary>
        private async Task StageAsync(CodeRunRequest request, CancellationToken ct)
        {
            var taskId = request.TaskId;
            var stage = StageName(taskId);
            await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = stage,
                Image = request.Image,
                Labels = Labels(taskId, "stage"),
                NetworkDisabled = true,
                HostConfig = new HostConfig
                {
                    NetworkMode = "none",
                    LogConfig = new LogConfig { Type = "none" },
                    Mounts =
                    [
                        VolumeMount(DataVolume(taskId), "/stage/data", readOnly: false),
                        VolumeMount(CodeVolume(taskId), "/stage/code", readOnly: false),
                        VolumeMount(OutputsVolume(taskId), "/outputs", readOnly: false)
                    ]
                }
            }, ct);

            using (var tar = await BuildTarAsync(request.Files, ct))
            {
                await _docker.Containers.ExtractArchiveToContainerAsync(stage,
                    new ContainerPathStatParameters { Path = "/stage", AllowOverwriteDirWithFile = false }, tar, ct);
            }

            await _docker.Containers.RemoveContainerAsync(stage, new ContainerRemoveParameters { Force = true }, ct);
        }

        private async Task CreateRunAsync(CodeRunRequest request, CancellationToken ct)
        {
            var taskId = request.TaskId;
            var labels = Labels(taskId, RunRole);
            labels[DatasetShaLabel] = request.DatasetSha256;
            await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = RunName(taskId),
                Image = request.Image,
                // Built here from the dataset name; never the task's command, env or other executor fields.
                Cmd = ["run-analysis", "/code/analysis.py", $"/data/{request.DatasetName}", ResultPath],
                User = RunUser,
                Labels = labels,
                NetworkDisabled = true,
                HostConfig = new HostConfig
                {
                    NetworkMode = "none",
                    ReadonlyRootfs = true,
                    CapDrop = ["ALL"],
                    SecurityOpt = ["no-new-privileges"],
                    PidsLimit = PidsLimit,
                    NanoCPUs = NanoCpus(request.CpuCores),
                    Memory = request.MemoryBytes,
                    MemorySwap = request.MemoryBytes,
                    Tmpfs = new Dictionary<string, string> { ["/tmp"] = $"rw,noexec,nosuid,size={request.TmpBytes}" },
                    // No file the code writes, in /outputs (a volume without a size limit) or /tmp, can be larger than
                    // the run's disk allowance. Many smaller files are not capped (known limit of the PoC).
                    Ulimits = [new Ulimit { Name = "fsize", Soft = request.TmpBytes, Hard = request.TmpBytes }],
                    LogConfig = new LogConfig { Type = "none" },
                    Mounts =
                    [
                        VolumeMount(DataVolume(taskId), "/data", readOnly: true),
                        VolumeMount(CodeVolume(taskId), "/code", readOnly: true),
                        VolumeMount(OutputsVolume(taskId), "/outputs", readOnly: false)
                    ]
                }
            }, ct);
        }

        private static Mount VolumeMount(string volume, string target, bool readOnly) =>
            new() { Type = "volume", Source = volume, Target = target, ReadOnly = readOnly };

        /// <summary>
        /// One tar of the staged files, relative to /stage: files 0444 and folders 0555, owned by root, so the code
        /// (user 1001) can read them and change nothing even if a mount were writable.
        /// </summary>
        private static async Task<MemoryStream> BuildTarAsync(IReadOnlyList<StagedFile> files, CancellationToken ct)
        {
            var output = new MemoryStream();
            await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
            {
                var folders = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in files)
                {
                    var path = CheckedPath(file.RelativePath);
                    var segments = path.Split('/');
                    for (var i = 1; i < segments.Length; i++)
                    {
                        var folder = string.Join('/', segments[..i]) + "/";
                        if (folders.Add(folder))
                        {
                            await writer.WriteEntryAsync(Entry(TarEntryType.Directory, folder, DirectoryMode), ct);
                        }
                    }

                    var entry = Entry(TarEntryType.RegularFile, path, FileMode);
                    entry.DataStream = file.Content;
                    await writer.WriteEntryAsync(entry, ct);
                }
            }

            output.Position = 0;
            return output;
        }

        private static PaxTarEntry Entry(TarEntryType type, string name, UnixFileMode mode) =>
            new(type, name) { Mode = mode, Uid = 0, Gid = 0 };

        /// <summary>A staged path must be under data/ or code/, with no empty, "." or ".." segment.</summary>
        private static string CheckedPath(string relativePath)
        {
            var segments = relativePath.Split('/');
            if (segments.Length < 2 || segments[0] is not ("data" or "code") ||
                segments.Any(s => s is "" or "." or ".."))
            {
                throw new ArgumentException($"Staged file path '{relativePath}' is not under data/ or code/");
            }

            return relativePath;
        }

        public async Task StopAsync(string taskId, CancellationToken ct)
        {
            // Killed at once: Python as the container's first process ignores SIGTERM, so a stop would wait Docker's
            // grace period for nothing.
            try
            {
                await _docker.Containers.KillContainerAsync(RunName(taskId), new ContainerKillParameters { Signal = "KILL" }, ct);
                await _docker.Containers.WaitContainerAsync(RunName(taskId), ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                // Gone, or not running any more.
            }
        }

        public async Task<ResultFileRead> ReadResultAsync(string taskId, int maxBytes, CancellationToken ct)
        {
            GetArchiveFromContainerResponse archive;
            try
            {
                archive = await _docker.Containers.GetArchiveFromContainerAsync(RunName(taskId),
                    new GetArchiveFromContainerParameters { Path = ResultPath }, statOnly: false, ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new ResultFileRead(null, false);
            }

            await using var stream = archive.Stream;
            await using var reader = new TarReader(stream);
            var entry = await reader.GetNextEntryAsync(copyData: false, ct);
            if (entry == null)
            {
                return new ResultFileRead(null, false);
            }

            // A link or folder the code made is not a result file: empty bytes, so the check finds it invalid.
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream == null)
            {
                return new ResultFileRead([], false);
            }

            if (entry.Length > maxBytes)
            {
                return new ResultFileRead(null, true);
            }

            var buffer = new byte[maxBytes + 1];
            var read = 0;
            int count;
            while (read < buffer.Length && (count = await entry.DataStream.ReadAsync(buffer.AsMemory(read), ct)) > 0)
            {
                read += count;
            }

            return read > maxBytes ? new ResultFileRead(null, true) : new ResultFileRead(buffer[..read], false);
        }

        public async Task RemoveAsync(string taskId, CancellationToken ct)
        {
            foreach (var container in new[] { RunName(taskId), StageName(taskId) })
            {
                try
                {
                    await _docker.Containers.RemoveContainerAsync(container, new ContainerRemoveParameters { Force = true }, ct);
                }
                catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }

            foreach (var volume in new[] { DataVolume(taskId), CodeVolume(taskId), OutputsVolume(taskId) })
            {
                try
                {
                    await _docker.Volumes.RemoveAsync(volume, force: true, ct);
                }
                catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }
        }

        /// <summary>A Docker state time; null when unset (Docker's zero time 0001-01-01) or unreadable.</summary>
        public static DateTimeOffset? ParseDockerTime(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Docker writes nanoseconds; .NET reads at most seven fraction digits.
            var trimmed = System.Text.RegularExpressions.Regex.Replace(text, @"(\.\d{7})\d+", "$1");
            if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) || time.Year <= 1)
            {
                return null;
            }

            return time;
        }

        private static IDictionary<string, IDictionary<string, bool>> LabelFilter(string label) =>
            new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [label] = true }
            };
    }
}
