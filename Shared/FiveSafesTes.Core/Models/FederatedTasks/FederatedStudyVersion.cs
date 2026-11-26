using System.ComponentModel.DataAnnotations.Schema;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// One submission of a study, numbered per study. It fans out to one <see cref="FederatedTask"/> per hospital (a part).
    /// </summary>
    public class FederatedStudyVersion : BaseModel
    {
        public int Id { get; set; }
        public Guid VersionId { get; set; }
        public string StudyId { get; set; } = string.Empty;

        /// <summary>1 for the first submission of the study, then one more each time.</summary>
        public int Version { get; set; }

        /// <summary>The normalised hospital list as submitted, joined with <c>|</c>.</summary>
        public string Sites { get; set; } = string.Empty;

        public string CodeSha256 { get; set; } = string.Empty;

        /// <summary>The submitted TES task. Written once, at intake: nothing may change it afterwards (init-only; ANZE-6045).</summary>
        public string TesTaskJson { get; init; } = string.Empty;

        /// <summary>UTC.</summary>
        public DateTime SubmittedAt { get; set; }

        /// <summary>The submitter's preferred_username, from their token at intake.</summary>
        public string? SubmittedBy { get; set; }

        /// <summary>UTC; <see cref="SubmittedAt"/> plus the study timeout configured at intake. Later config changes don't move it.</summary>
        public DateTime DeadlineAt { get; set; }

        /// <summary>Null until the sweep or an admin cancel decides the version (<see cref="FederatedStudyOutcomes.Decide"/>).</summary>
        public FederatedStudyOutcome? Outcome { get; set; }

        /// <summary>One of <see cref="FederatedStudyOutcomeReasons"/>; set with <see cref="Outcome"/>.</summary>
        public string? OutcomeReason { get; set; }

        /// <summary>A sentence for people saying why the version ended.</summary>
        public string? OutcomeMessage { get; set; }

        /// <summary>UTC; when the outcome was set.</summary>
        public DateTime? OutcomeAt { get; set; }

        /// <summary>UTC; when the version became ready for aggregation (reset by an admin retry). The aggregation timeout counts from here.</summary>
        public DateTime? ReadyAt { get; set; }

        /// <summary>UTC; when the aggregator reported that it started pooling.</summary>
        public DateTime? AggregationStartedAt { get; set; }

        /// <summary>Why aggregation failed (outcome <see cref="FederatedStudyOutcome.Failed"/>).</summary>
        public string? AggregationErrorCode { get; set; }

        public string? PooledResultBucket { get; set; }
        public string? PooledResultKey { get; set; }

        /// <summary>SHA-256 of the stored pooled result document.</summary>
        public string? PooledResultSha256 { get; set; }

        public virtual List<FederatedTask> Parts { get; set; } = [];

        /// <summary>
        /// Open until there is an outcome; then Canceling while a part is still being cleaned up at its hospital, and Ended
        /// once every part is final. Only an open version blocks a new one. Any query whose result's State is read must
        /// <c>Include(x =&gt; x.Parts)</c>, or a decided version with no parts loaded would wrongly be Ended.
        /// </summary>
        [NotMapped]
        public FederatedStudyVersionState State =>
            Outcome is null ? FederatedStudyVersionState.Open
            : Parts.All(p => FederatedTaskPhases.IsFinal(p.Phase)) ? FederatedStudyVersionState.Ended
            : FederatedStudyVersionState.Canceling;
    }
}
