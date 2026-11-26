using System.Security.Claims;
using FiveSafesTes.Core.Models;
using Newtonsoft.Json;
using Submission.Api.Repositories.DbContexts;

namespace Submission.Api.Services
{
    /// <summary>
    /// Audit-log entries for federated studies (ANZE-6133). Added, not saved: the caller saves the entry in the same save
    /// as the change it records, so there is never an entry without the change or a change without its entry.
    /// </summary>
    public static class FederatedAudit
    {
        /// <summary>The federated study entries, with their wire names.</summary>
        public static readonly IReadOnlyDictionary<LogType, string> Types = new Dictionary<LogType, string>
        {
            [LogType.FederatedStudySubmitted] = "study_submitted",
            [LogType.FederatedStudyCanceled] = "study_canceled",
            [LogType.FederatedAggregationRetried] = "aggregation_retried",
            [LogType.FederatedPooledResultStored] = "pooled_result_stored"
        };

        public static void Add(ApplicationDbContext db, LogType type, ClaimsPrincipal user, HttpContext? http, object details)
        {
            db.AuditLogs.Add(new AuditLog
            {
                LogType = type,
                LoggedInUserName = user.FindFirst("preferred_username")?.Value,
                IPaddress = http?.Connection.RemoteIpAddress?.ToString(),
                HistoricFormData = JsonConvert.SerializeObject(details),
                Date = DateTime.UtcNow
            });
        }
    }
}
