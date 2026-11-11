using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BL.Models.ViewModels
{
    public class EgressReview
    {
        public string SubId { get; set; }
        public List<EgressResult> FileResults { get; set; }

        public string OutputBucket { get; set; }

        // Federated reviews only; null for native egress.
        public string? Reviewer { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? Reason { get; set; }
        public string? ResultSha256 { get; set; }
    }

    public class EgressResult
    {
        public string? FileName { get; set; }
        public bool Approved { get; set; } 
    }
}
