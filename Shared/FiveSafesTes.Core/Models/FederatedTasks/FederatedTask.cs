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

        public FederatedTaskPhase Phase { get; set; } = FederatedTaskPhase.Queued;

        public virtual List<FederatedTaskPhaseChange> PhaseChanges { get; set; } = [];

        /// <summary>
        /// Moves the task to a phase without an error (queued, running, waiting, released, canceling or canceled), updating its TES state
        /// and recording the change. Rejections go through <see cref="Reject"/>, failures through <see cref="Fail"/>.
        /// </summary>
        public void MoveTo(FederatedTaskPhase phase, DateTime atUtc)
        {
            if (phase == FederatedTaskPhase.Failed || FederatedTaskPhases.IsRejection(phase))
            {
                throw new ArgumentException($"use {nameof(Reject)} or {nameof(Fail)} for phase {phase}", nameof(phase));
            }

            Record(phase, FederatedTaskPhases.ToTesState(phase), atUtc, null, null);
        }

        /// <summary>
        /// Cancels the task as the phase graph allows: a queued task to canceled at once, a running or waiting task to
        /// canceling until its hospital confirms. Returns what happened; only <see cref="FederatedTaskTransition.Apply"/>
        /// changes the task.
        /// </summary>
        public FederatedTaskTransition RequestCancel(DateTime atUtc)
        {
            var next = Phase == FederatedTaskPhase.Queued ? FederatedTaskPhase.Canceled : FederatedTaskPhase.Canceling;
            var transition = FederatedTaskPhases.Check(Phase, next);
            if (transition == FederatedTaskTransition.Apply)
            {
                MoveTo(next, atUtc);
            }

            return transition;
        }

        /// <summary>Moves the task to a rejected phase (TES CANCELED) with the reason.</summary>
        public void Reject(FederatedTaskPhase phase, DateTime atUtc, string errorCode, string errorMessage)
        {
            if (!FederatedTaskPhases.IsRejection(phase))
            {
                throw new ArgumentException($"{phase} is not a rejection", nameof(phase));
            }

            Record(phase, FederatedTaskPhases.ToTesState(phase), atUtc, errorCode, errorMessage);
        }

        /// <summary>Moves the task to failed with <paramref name="state"/> (EXECUTOR_ERROR or SYSTEM_ERROR) and the reason.</summary>
        public void Fail(DateTime atUtc, TesState state, string errorCode, string? errorMessage)
        {
            if (state is not (TesState.EXECUTORERROREnum or TesState.SYSTEMERROREnum))
            {
                throw new ArgumentException("a failed task must be EXECUTOR_ERROR or SYSTEM_ERROR", nameof(state));
            }

            Record(FederatedTaskPhase.Failed, state, atUtc, errorCode, errorMessage);
        }

        private void Record(FederatedTaskPhase phase, TesState state, DateTime atUtc, string? errorCode, string? errorMessage)
        {
            Phase = phase;
            State = state;
            if (FederatedTaskPhases.IsFinal(phase))
            {
                CompletedAt = atUtc;
            }

            ErrorCode = errorCode;
            ResultErrorMessage = errorMessage;
            PhaseChanges.Add(new FederatedTaskPhaseChange
            {
                Phase = phase,
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

            var metadata = new Dictionary<string, string> { ["phase"] = FederatedTaskPhases.ToWire(Phase) };
            DateTime? runningAt = null;
            DateTime? finalAt = null;
            foreach (var change in PhaseChanges.OrderBy(c => c.At).ThenBy(c => c.Id))
            {
                var key = FederatedTaskPhases.ToWire(change.Phase) + "_at";
                // A phase is recorded once on Postgres, where a failed save rolls back. The in-memory test provider
                // can keep a row from a save that lost a race, so the first time a phase was reached wins.
                if (metadata.ContainsKey(key))
                {
                    continue;
                }

                metadata[key] = DateTime.SpecifyKind(change.At, DateTimeKind.Utc).ToString("O");
                if (change.Phase == FederatedTaskPhase.Running)
                {
                    runningAt = change.At;
                }

                if (change.Phase == Phase && FederatedTaskPhases.IsFinal(Phase))
                {
                    finalAt = change.At;
                }
            }

            if (Phase == FederatedTaskPhase.Released)
            {
                metadata["result_bucket"] = ResultBucket ?? "";
                metadata["result_key"] = ResultKey ?? "";
                metadata["result_sha256"] = ResultSha256 ?? "";
            }
            else if (Phase == FederatedTaskPhase.Failed || FederatedTaskPhases.IsRejection(Phase))
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
