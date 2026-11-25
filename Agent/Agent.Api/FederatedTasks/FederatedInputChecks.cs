using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Serilog;

namespace Agent.Api.FederatedTasks
{
    public enum InputCheckKind
    {
        /// <summary>Every check passed; the task may run.</summary>
        Passed,

        /// <summary>A check failed; the task is reported as rejected by the input checks.</summary>
        Rejected,

        /// <summary>The TRE cannot judge the task now; it keeps its phase and is checked again at the next poll.</summary>
        Held
    }

    /// <summary>
    /// What the input checks decided. On a pass, the key of the dataset file to read. Whenever the code scan ran, the
    /// version and SHA-256 of the rule file it used.
    /// </summary>
    public sealed record InputCheckOutcome(InputCheckKind Kind, string? FileKey, string? ErrorCode, string? ErrorMessage)
    {
        public string? ScanRulesVersion { get; init; }
        public string? ScanRulesSha256 { get; init; }

        public static InputCheckOutcome Pass(string fileKey) => new(InputCheckKind.Passed, fileKey, null, null);

        public static InputCheckOutcome Reject(string code, string message) => new(InputCheckKind.Rejected, null, code, message);

        public static InputCheckOutcome Hold() => new(InputCheckKind.Held, null, null, null);
    }

    /// <summary>
    /// The hospital's automatic checks on a parsed task, run before it is marked running. The first failure wins.
    /// Reads no data values. A dataset store failure throws; the caller decides whether to retry or give up.
    /// The format check (FederatedTesProfile.Parse) and the TreName routing run before this step, in the caller: the
    /// task must be parsed to be checked at all.
    /// </summary>
    public sealed class FederatedInputChecks
    {
        private readonly IDatasetReader _reader;
        private readonly FederatedTaskSettings _settings;
        private readonly ICodeScanner _scanner;

        public FederatedInputChecks(IDatasetReader reader, FederatedTaskSettings settings, ICodeScanner scanner)
        {
            _reader = reader;
            _settings = settings;
            _scanner = scanner;
        }

        /// <summary>
        /// False when the site has not set ApprovedImages. That is a TRE-wide setting, not a property of one task: every
        /// task that reaches the checks is held until it is set, because a forgotten setting must not reject anything.
        /// </summary>
        public bool IsConfigured => _settings.ApprovedImages.Length > 0;

        /// <summary>
        /// Each check returns a rejection, or null when the task passes it. The first rejection wins.
        /// </summary>
        public async Task<InputCheckOutcome> RunAsync(TesTask task, FederatedTaskSpec spec, CancellationToken ct)
        {
            if (!IsConfigured) return InputCheckOutcome.Hold();

            // The image comes before the dataset, so a TRE that has not approved it never lists its bucket for the task.
            if (CheckImage(task) is { } imageRejected) return imageRejected;

            // The code is scanned before the dataset is resolved, so code the hospital refuses never touches its bucket.
            var scan = await _scanner.ScanAsync(CodeOf(task), ct);
            if (scan.Kind == CodeScanKind.RulesUnavailable)
            {
                // A broken rule file is the hospital's config, not the researcher's fault: hold, don't reject.
                Log.Error("Federated tasks: the code-scan rules are unavailable ({Detail}); task {TaskId} is held", scan.Detail, task.Id);
                return InputCheckOutcome.Hold();
            }

            return (await CheckAfterScanAsync(scan, spec, task.Id, ct)) with
            {
                ScanRulesVersion = scan.RulesVersion,
                ScanRulesSha256 = scan.RulesSha256
            };
        }

        private async Task<InputCheckOutcome> CheckAfterScanAsync(CodeScanResult scan, FederatedTaskSpec spec, string? taskId,
            CancellationToken ct)
        {
            if (scan.Kind == CodeScanKind.ScannerFailed)
            {
                Log.Error("Federated tasks: the code scan of task {TaskId} could not run: {Detail}", taskId, scan.Detail);
                return InputCheckOutcome.Reject(FederatedTaskErrorCodes.InternalError, "the code scan could not run");
            }

            if (scan.Kind == CodeScanKind.Findings)
                return InputCheckOutcome.Reject(FederatedTaskErrorCodes.UnsafeCode, CodeScanMessages.Format(scan.Findings));

            var dataset = await _reader.ResolveAsync(spec.Dataset, ct);
            if (CheckDataset(dataset) is { } datasetRejected) return datasetRejected;

            // The researcher's code gets the schema with the data, so a dataset without one cannot run.
            if (dataset.SchemaKey == null)
            {
                return InputCheckOutcome.Reject(FederatedTaskErrorCodes.SchemaNotFound,
                    $"no schema.json found for '{spec.Dataset}'");
            }

            return InputCheckOutcome.Pass(dataset.FileKey!);
        }

        /// <summary>The researcher's code: the inline content of the input at the profile's code path.</summary>
        private static string CodeOf(TesTask task) =>
            task.Inputs?.FirstOrDefault(i => i.Path == FederatedTesProfile.CodePath)?.Content ?? string.Empty;

        /// <summary>
        /// The task's executor image, or null when the task has none. Safe on a task that was not parsed against the
        /// profile, such as one whose saved result is released without running the checks again.
        /// </summary>
        public static string? ImageOf(TesTask task) => task.Executors?.FirstOrDefault()?.Image;

        public bool IsImageApproved(string? image) =>
            image != null && _settings.ApprovedImages.Contains(image, StringComparer.Ordinal);

        private InputCheckOutcome? CheckImage(TesTask task)
        {
            var image = ImageOf(task);
            return IsImageApproved(image)
                ? null
                : InputCheckOutcome.Reject(FederatedTaskErrorCodes.ImageNotApproved, $"image '{image}' is not approved at this TRE");
        }

        private static InputCheckOutcome? CheckDataset(DatasetResolveResult dataset) =>
            dataset.IsSuccess ? null : InputCheckOutcome.Reject(dataset.ErrorCode!, dataset.ErrorMessage!);
    }
}
