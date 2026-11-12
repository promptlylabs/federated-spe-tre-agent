using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using FiveSafesTes.Core.Models.Enums;

namespace FiveSafesTes.Core.Models
{
    public class EgressSubmission
    {
        public int Id { get; set; }
        public string? SubmissionId { get; set; }
        public EgressStatus Status { get; set; }
        public string? OutputBucket { get; set; }

       

        public DateTime? Completed { get; set; }
        public string? Reviewer { get; set; }
        public virtual List<EgressFile> Files { get; set; }

        public string? tesId { get; set; }

        public string? Name { get; set; } 

        // Federated review context (set by the TRE Agent; null on native egress rows).
        public string? Kind { get; set; }
        public virtual FederatedStudy? Study { get; set; }
        public string? Code { get; set; }
        public string? ResultContent { get; set; }
        public string? ResultSha256 { get; set; }
        public string? DecisionReason { get; set; }
        public bool DecisionDelivered { get; set; }

        [NotMapped]
        public bool IsFederated => Kind == "federated";
        
        public string GetS3BucketUrl(string minioBaseUrl = "http://localhost:9003", string bucketPath = "/rustfs/console/browser/?bucket=")
        {
            return string.IsNullOrEmpty(OutputBucket) 
                ? "#" 
                : $"{minioBaseUrl}{bucketPath}{OutputBucket}";
        }
        
        public string EgressStatusDisplay
        {
            get
            {
                var enumType = typeof(EgressStatus);
                var memberInfo = enumType.GetMember(Status.ToString());
                var displayAttribute = memberInfo.FirstOrDefault()?.GetCustomAttribute<DisplayAttribute>();

                return displayAttribute?.Name ?? Status.ToString();
            }
        }

        public string EgressID()
        {
            if (string.IsNullOrEmpty(tesId))
            {
                return SubmissionId;
            }
            else
            {
                return tesId;
            }
        }
    }
}
