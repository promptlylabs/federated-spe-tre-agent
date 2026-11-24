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

        /// <summary>UTC; set when the orchestrator cancelled the version.</summary>
        public DateTime? CanceledAt { get; set; }

        public virtual List<FederatedTask> Parts { get; set; } = [];

        /// <summary>
        /// Ended when every part is final, otherwise Canceling once cancelled, otherwise Open. Derived from
        /// <see cref="Parts"/>: any query whose result's State is read must <c>Include(x =&gt; x.Parts)</c>, or a
        /// version with no parts loaded would wrongly be Ended.
        /// </summary>
        [NotMapped]
        public FederatedStudyVersionState State =>
            Parts.All(p => FederatedTaskPhases.IsFinal(p.Phase)) ? FederatedStudyVersionState.Ended
            : CanceledAt is not null ? FederatedStudyVersionState.Canceling
            : FederatedStudyVersionState.Open;
    }
}
