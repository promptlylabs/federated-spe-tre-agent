using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;

namespace Agent.Api.FederatedTasks
{
    public enum InputCheckKind
    {
        /// <summary>Every check passed; the task may run.</summary>
        Passed,

        /// <summary>A check failed; the task is reported as rejected by the input checks.</summary>
        Rejected,

        /// <summary>The TRE cannot judge the task now; it keeps its step and is checked again at the next poll.</summary>
        Held
    }

    /// <summary>What the input checks decided. On a pass, the key of the dataset file to read.</summary>
    public sealed record InputCheckOutcome(InputCheckKind Kind, string? FileKey, string? ErrorCode, string? ErrorMessage)
    {
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

        public FederatedInputChecks(IDatasetReader reader, FederatedTaskSettings settings)
        {
            _reader = reader;
            _settings = settings;
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

            var dataset = await _reader.ResolveAsync(spec.Dataset, ct);
            if (CheckDataset(dataset) is { } datasetRejected) return datasetRejected;

            return InputCheckOutcome.Pass(dataset.FileKey!);
        }

        private InputCheckOutcome? CheckImage(TesTask task)
        {
            // The profile has already checked there is exactly one executor.
            var image = task.Executors[0].Image;
            return _settings.ApprovedImages.Contains(image, StringComparer.Ordinal)
                ? null
                : InputCheckOutcome.Reject(FederatedTaskErrorCodes.ImageNotApproved, $"image '{image}' is not approved at this TRE");
        }

        private static InputCheckOutcome? CheckDataset(DatasetResolveResult dataset) =>
            dataset.IsSuccess ? null : InputCheckOutcome.Reject(dataset.ErrorCode!, dataset.ErrorMessage!);
    }
}
