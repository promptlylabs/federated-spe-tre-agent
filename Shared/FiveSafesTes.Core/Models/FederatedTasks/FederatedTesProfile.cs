using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FiveSafesTes.Core.Models.Tes;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// The restricted TES "federated profile": validates a <see cref="TesTask"/> and converts it to a
    /// <see cref="FederatedTaskSpec"/>, and builds a task that passes (<see cref="BuildCodeTask"/>).
    /// </summary>
    public static class FederatedTesProfile
    {
        public const string DatasetUrlScheme = "dataset://";

        public const string SitesTag = "fed.sites";
        public const string StudyIdTag = "fed.study_id";
        public const string CodeSha256Tag = "fed.code_sha256";

        // Code tasks: the researcher's file, run by run-analysis in an approved runner image.
        public const string RunAnalysisCommand = "run-analysis";
        public const string CodePath = "/code/analysis.py";
        public const string DataPathPrefix = "/data/";
        public const string ResultPath = "/outputs/result.json";

        /// <summary>
        /// The only output url a code task may give. TES requires a url; a real address could let a runtime upload the
        /// result past output approval, so none is accepted. The result leaves through output approval and ReportResult.
        /// </summary>
        public const string ResultUrl = "result://";
        public const int MaxCodeBytes = 262144;

        private static readonly Regex Sha256Pattern = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);

        /// <summary>
        /// Applies the code-task rules in order; the first failure wins. Every federated task is a code task: the
        /// researcher's file, run by run-analysis in a runner image. <paramref name="intake"/> carries the
        /// orchestrator's approved runner images and resource limits; the Agent passes none, and its own ApprovedImages
        /// decides which images it runs.
        /// </summary>
        public static FederatedTaskParseResult Parse(TesTask task, FederatedIntakeLimits? intake = null)
        {
            // Executors
            if (task.Executors == null || task.Executors.Count != 1)
                return Fail(FederatedTaskErrorCodes.InvalidTask, "task must have exactly one executor");
            var executor = task.Executors[0];
            if (executor == null)
                return Fail(FederatedTaskErrorCodes.InvalidTask, "executor must not be null");

            return ParseCodeTask(task, executor, intake);
        }

        // Code tasks: exactly `run-analysis /code/analysis.py /data/<dataset> /outputs/result.json`, the code inline.
        private static FederatedTaskParseResult ParseCodeTask(TesTask task, TesExecutor executor, FederatedIntakeLimits? intake)
        {
            // Image: at intake it must be an approved runner image. The Agent checks its own ApprovedImages later.
            if (intake != null)
            {
                if (intake.ApprovedRunnerImages.Count == 0)
                    return Fail(FederatedTaskErrorCodes.InvalidTask, "no runner image is approved");
                if (!intake.ApprovedRunnerImages.Contains(executor.Image, StringComparer.Ordinal))
                    return Fail(FederatedTaskErrorCodes.InvalidTask,
                        "executor image must be an approved runner image");
            }

            // Nothing that could change how the code runs.
            if (executor.Env is { Count: > 0 } || !string.IsNullOrEmpty(executor.Workdir) || !string.IsNullOrEmpty(executor.Stdin)
                || !string.IsNullOrEmpty(executor.Stdout) || !string.IsNullOrEmpty(executor.Stderr))
                return Fail(FederatedTaskErrorCodes.InvalidTask, "executor env, workdir, stdin, stdout and stderr must not be set");
            if (task.Volumes is { Count: > 0 })
                return Fail(FederatedTaskErrorCodes.InvalidTask, "task must not declare volumes");

            // Inputs: the code and the dataset, in either order. Checked before the command, which names the dataset path.
            var inputs = task.Inputs is { Count: 2 } && task.Inputs.All(i => i != null) ? task.Inputs : [];
            var codeInputs = inputs.Where(i => i.Path == CodePath).ToList();
            var code = codeInputs.Count == 1 ? codeInputs[0] : null;
            var dataInputs = inputs
                .Where(i => i != code && i.Url != null && i.Url.StartsWith(DatasetUrlScheme, StringComparison.Ordinal))
                .ToList();
            var data = code != null && dataInputs.Count == 1 ? dataInputs[0] : null;
            if (code == null || data == null)
                return Fail(FederatedTaskErrorCodes.InvalidTask, "task must have one code input and one dataset input");
            if (code.Type != TesFileType.FILEEnum)
                return Fail(FederatedTaskErrorCodes.InvalidTask, "the code input must be a FILE");
            if (string.IsNullOrEmpty(code.Content) || !string.IsNullOrEmpty(code.Url))
                return Fail(FederatedTaskErrorCodes.InvalidTask, "the code input must have content and no url");
            var codeBytes = Encoding.UTF8.GetBytes(code.Content);
            if (codeBytes.Length > MaxCodeBytes)
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"code must be at most {MaxCodeBytes} bytes");

            var dataset = data.Url!.Substring(DatasetUrlScheme.Length);
            if (!FederatedTaskNames.IsValidDatasetName(dataset))
                return Fail(FederatedTaskErrorCodes.InvalidDatasetName, "invalid dataset name");
            var dataPath = DataPathPrefix + dataset;
            if (data.Path != dataPath)
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"the dataset input path must be {dataPath}");
            if (data.Type != TesFileType.DIRECTORYEnum)
                return Fail(FederatedTaskErrorCodes.InvalidTask, "the dataset input must be a DIRECTORY");
            // In TES, content wins over url: a dataset input with content would bring its own data.
            if (!string.IsNullOrEmpty(data.Content))
                return Fail(FederatedTaskErrorCodes.InvalidTask, "the dataset input must not have content");

            // Command: exactly run-analysis and the three paths. No --debug: its traceback can hold data values.
            string[] expected = [RunAnalysisCommand, CodePath, dataPath, ResultPath];
            if (executor.Command == null || !executor.Command.SequenceEqual(expected, StringComparer.Ordinal))
                return Fail(FederatedTaskErrorCodes.InvalidTask,
                    $"executor command must be {RunAnalysisCommand} {CodePath} {DataPathPrefix}<dataset> {ResultPath}");

            // Outputs: the one file the result may leave in, with a url that points nowhere.
            if (task.Outputs is not { Count: 1 } || task.Outputs[0] is not { } output
                || output.Path != ResultPath || output.Type != TesFileType.FILEEnum)
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"task must have exactly one output, {ResultPath}");
            if (output.Url != ResultUrl)
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"the output url must be {ResultUrl}");

            // Resources: only cpu, memory and disk; the rest would be passed to a runtime unchecked.
            if (task.Resources is { } given && (given.Zones is { Count: > 0 } || given.BackendParameters is { Count: > 0 }
                    || given.Preemptible != null || given.BackendParametersStrict != null))
                return Fail(FederatedTaskErrorCodes.InvalidTask, "resources may only set cpu_cores, ram_gb and disk_gb");

            // Resource limits: checked at intake only, and only those given.
            if (intake != null && task.Resources is { } resources)
            {
                if (OutOfRange("cpu_cores", resources.CpuCores, intake.MaxCpuCores) is { } cpu) return cpu;
                if (OutOfRange("ram_gb", resources.RamGb, intake.MaxRamGb) is { } ram) return ram;
                if (OutOfRange("disk_gb", resources.DiskGb, intake.MaxDiskGb) is { } disk) return disk;
            }

            // Tags: the TRE and study, plus the hash of the code that runs.
            if (ReadTags(task, out var tags, out var tre, out var studyId) is { } tagFailure)
                return tagFailure;
            if (!tags.TryGetValue(CodeSha256Tag, out var codeSha) || codeSha is null || !Sha256Pattern.IsMatch(codeSha))
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"tag '{CodeSha256Tag}' must be a lowercase hex SHA-256");
            if (codeSha != Convert.ToHexStringLower(SHA256.HashData(codeBytes)))
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"tag '{CodeSha256Tag}' does not match the code");

            var spec = new FederatedTaskSpec(RunAnalysisCommand, dataset, studyId, tre, codeSha);
            return new FederatedTaskParseResult(spec, null, null);
        }

        private static FederatedTaskParseResult? OutOfRange(string field, double? value, double max)
        {
            if (value is not { } v || (double.IsFinite(v) && v > 0 && v <= max))
                return null;
            return Fail(FederatedTaskErrorCodes.InvalidTask,
                $"resources.{field} must be more than 0 and at most {max.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// The fed.* tags every task needs. <paramref name="tre"/> is trimmed and lowercased. Returns null when they
        /// are valid, otherwise the failure.
        /// </summary>
        private static FederatedTaskParseResult? ReadTags(
            TesTask task, out Dictionary<string, string> tags, out string tre, out string studyId)
        {
            tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            tre = studyId = string.Empty;
            foreach (var tag in task.Tags ?? [])
            {
                if (!tags.TryAdd(tag.Key, tag.Value))
                    return Fail(FederatedTaskErrorCodes.InvalidTask, "duplicate tag key");
            }

            // Exactly one TRE: non-empty after trimming, no '|' separator. Existence is checked later.
            if (!tags.TryGetValue(SitesTag, out var sites) || string.IsNullOrWhiteSpace(sites) || sites.Contains('|'))
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"tag '{SitesTag}' must name exactly one TRE");
            if (!tags.TryGetValue(StudyIdTag, out var study) || string.IsNullOrWhiteSpace(study))
                return Fail(FederatedTaskErrorCodes.InvalidTask, $"tag '{StudyIdTag}' is required");
            if (sites.Length > FederatedResultLimits.MaxStringLength || study.Length > FederatedResultLimits.MaxStringLength)
                return Fail(FederatedTaskErrorCodes.InvalidTask,
                    $"tags '{SitesTag}' and '{StudyIdTag}' must be at most {FederatedResultLimits.MaxStringLength} characters");

            tre = sites.Trim().ToLowerInvariant();
            studyId = study;
            return null;
        }

        /// <summary>
        /// Builds a code task that passes <see cref="Parse"/>: the exact run-analysis command, the code inline, the
        /// dataset, the result output and the fed.* tags with the code's SHA-256.
        /// </summary>
        public static TesTask BuildCodeTask(string dataset, string code, string studyId, string treName,
            string image = "python-runner:1")
        {
            var dataPath = DataPathPrefix + dataset;
            return new TesTask
            {
                Executors = [new TesExecutor { Image = image, Command = [RunAnalysisCommand, CodePath, dataPath, ResultPath] }],
                Inputs =
                [
                    new TesInput { Path = CodePath, Type = TesFileType.FILEEnum, Content = code },
                    new TesInput { Url = DatasetUrlScheme + dataset, Path = dataPath, Type = TesFileType.DIRECTORYEnum }
                ],
                Outputs = [new TesOutput { Path = ResultPath, Url = ResultUrl, Type = TesFileType.FILEEnum }],
                Tags = new Dictionary<string, string>
                {
                    [SitesTag] = treName,
                    [StudyIdTag] = studyId,
                    [CodeSha256Tag] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)))
                }
            };
        }

        private static FederatedTaskParseResult Fail(string code, string message) =>
            new(null, code, message);
    }
}
