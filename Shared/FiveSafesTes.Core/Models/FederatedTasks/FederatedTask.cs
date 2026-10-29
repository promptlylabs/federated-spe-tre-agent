using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
        public string StudyId { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Dataset { get; set; } = string.Empty;

        /// <summary>The submitted TES task, Newtonsoft-serialized.</summary>
        public string TesTaskJson { get; set; } = string.Empty;

        public TesState State { get; set; }

        /// <summary>UTC.</summary>
        public DateTime SubmittedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public decimal? ResultValue { get; set; }
        public int? ResultRowCount { get; set; }
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

        public TesTask ToTesTask()
        {
            var task = JsonConvert.DeserializeObject<TesTask>(TesTaskJson) ?? new TesTask();
            task.Id = TaskId.ToString();
            task.State = State;
            task.CreationTime = AsUtc(SubmittedAt);

            Dictionary<string, string>? metadata = State switch
            {
                TesState.COMPLETEEnum => new Dictionary<string, string>
                {
                    ["value"] = ResultValue?.ToString(CultureInfo.InvariantCulture) ?? "",
                    ["row_count"] = ResultRowCount?.ToString(CultureInfo.InvariantCulture) ?? ""
                },
                TesState.EXECUTORERROREnum or TesState.SYSTEMERROREnum => new Dictionary<string, string>
                {
                    ["error_code"] = ErrorCode ?? "",
                    ["error_message"] = ResultErrorMessage ?? ""
                },
                _ => null
            };

            if (metadata is not null)
            {
                if (DatasetSha256 is not null)
                {
                    metadata["dataset_sha256"] = DatasetSha256;
                }

                task.Logs =
                [
                    new TesTaskLog
                    {
                        EndTime = CompletedAt is { } completed ? AsUtc(completed) : null,
                        Metadata = metadata
                    }
                ];
            }

            return task;
        }

        private static DateTimeOffset AsUtc(DateTime value) =>
            new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
