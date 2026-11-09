using System.ComponentModel.DataAnnotations;
using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// A GA4GH TES task queued by the orchestrator for a TRE Agent, with its outcome.
    /// </summary>
    public class FederatedTask : BaseModel
    {
        public int Id { get; set; }
        public Guid TaskId { get; set; }
        public int TreId { get; set; }
        public virtual Tre? Tre { get; set; }

        /// <summary>
        /// The task's <c>tres</c> tag as enqueued (trimmed, lowercased). If the TRE is renamed later, the task no
        /// longer matches its TRE and is not handed to the Agent.
        /// </summary>
        public string TreName { get; set; } = string.Empty;
        public string StudyId { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Dataset { get; set; } = string.Empty;

        /// <summary>The submitted TES task, Newtonsoft-serialized.</summary>
        public string TesTaskJson { get; set; } = string.Empty;

        public TesState State { get; set; }

        /// <summary>UTC.</summary>
        public DateTime SubmittedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        /// <summary>Orchestrator bucket holding the result file; null until a result file is stored.</summary>
        [MaxLength(63)]
        public string? ResultBucket { get; set; }

        /// <summary>Object key of the result file in <see cref="ResultBucket"/>.</summary>
        [MaxLength(256)]
        public string? ResultKey { get; set; }

        /// <summary>Lowercase hex SHA-256 of the stored result file.</summary>
        [MaxLength(64)]
        public string? ResultSha256 { get; set; }
        public string? ErrorCode { get; set; }
        public string? ResultErrorMessage { get; set; }

        /// <summary>Lowercase hex SHA-256 of the dataset file the TRE read; null when it was never read.</summary>
        [MaxLength(64)]
        public string? DatasetSha256 { get; set; }

        /// <summary>Reserved for VER-3.</summary>
        public string? ManifestHash { get; set; }

        /// <summary>Reserved for VER-3.</summary>
        public string? SiteToken { get; set; }

        /// <summary>Reserved for DAAMS.</summary>
        public string? ApprovalReference { get; set; }

        public FederatedTaskStep Step { get; set; } = FederatedTaskStep.Queued;

        public List<FederatedTaskStepChange> StepChanges { get; set; } = [];

        /// <summary>Moves the task to <paramref name="step"/>, updating its TES state and recording the change.</summary>
        public void MoveTo(FederatedTaskStep step, DateTime atUtc, TesState? failedState = null, string? errorCode = null, string? errorMessage = null)
        {
            var state = FederatedTaskSteps.ToTesState(step, failedState);
            Step = step;
            State = state;
            if (FederatedTaskSteps.IsFinal(step))
            {
                CompletedAt = atUtc;
            }

            ErrorCode = errorCode;
            ResultErrorMessage = errorMessage;
            StepChanges.Add(new FederatedTaskStepChange
            {
                Step = step,
                At = atUtc,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            });
        }

        public TesTask ToTesTask()
        {
            var task = JsonConvert.DeserializeObject<TesTask>(TesTaskJson) ?? new TesTask();
            task.Id = TaskId.ToString();
            task.State = State;
            task.CreationTime = AsUtc(SubmittedAt);

            var metadata = new Dictionary<string, string> { ["step"] = FederatedTaskSteps.ToWire(Step) };
            DateTime? runningAt = null;
            DateTime? finalAt = null;
            foreach (var change in StepChanges.OrderBy(c => c.At).ThenBy(c => c.Id))
            {
                var key = FederatedTaskSteps.ToWire(change.Step) + "_at";
                if (metadata.ContainsKey(key))
                {
                    continue;
                }

                metadata[key] = DateTime.SpecifyKind(change.At, DateTimeKind.Utc).ToString("O");
                if (change.Step == FederatedTaskStep.Running)
                {
                    runningAt = change.At;
                }

                if (change.Step == Step && FederatedTaskSteps.IsFinal(Step))
                {
                    finalAt = change.At;
                }
            }

            if (Step == FederatedTaskStep.Released)
            {
                metadata["result_bucket"] = ResultBucket ?? "";
                metadata["result_key"] = ResultKey ?? "";
                metadata["result_sha256"] = ResultSha256 ?? "";
            }
            else if (Step is FederatedTaskStep.Failed or FederatedTaskStep.RejectedByInputChecks or FederatedTaskStep.RejectedAtOutputApproval)
            {
                metadata["error_code"] = ErrorCode ?? "";
                metadata["error_message"] = ResultErrorMessage ?? "";
            }

            if (DatasetSha256 is not null)
            {
                metadata["dataset_sha256"] = DatasetSha256;
            }

            task.Logs =
            [
                new TesTaskLog
                {
                    StartTime = runningAt is { } started ? AsUtc(started) : null,
                    EndTime = finalAt is { } ended ? AsUtc(ended) : null,
                    Metadata = metadata
                }
            ];

            return task;
        }

        private static DateTimeOffset AsUtc(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
