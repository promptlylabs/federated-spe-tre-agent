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

        public async Task<InputCheckOutcome> RunAsync(TesTask task, FederatedTaskSpec spec, CancellationToken ct)
        {
            // A forgotten setting must not reject anything: a rejection is final.
            if (_settings.ApprovedImages.Length == 0)
            {
                Log.Error("Federated tasks: FederatedTaskSettings:ApprovedImages is not configured; task {TaskId} is not checked or run", task.Id);
                return InputCheckOutcome.Hold();
            }

            // The image comes before the dataset, so a TRE that has not approved it never lists its bucket for the task.
            var image = CheckImage(task);
            if (image != null) return image;

            return await CheckDatasetAsync(spec, ct);
        }

        private InputCheckOutcome? CheckImage(TesTask task)
        {
            // The profile has already checked there is exactly one executor.
            var image = task.Executors[0].Image;
            return _settings.ApprovedImages.Contains(image, StringComparer.Ordinal)
                ? null
                : InputCheckOutcome.Reject(FederatedTaskErrorCodes.ImageNotApproved, $"image '{image}' is not approved at this TRE");
        }

        private async Task<InputCheckOutcome> CheckDatasetAsync(FederatedTaskSpec spec, CancellationToken ct)
        {
            var resolved = await _reader.ResolveAsync(spec.Dataset, ct);
            return resolved.IsSuccess
                ? InputCheckOutcome.Pass(resolved.FileKey!)
                : InputCheckOutcome.Reject(resolved.ErrorCode!, resolved.ErrorMessage!);
        }
    }
}
