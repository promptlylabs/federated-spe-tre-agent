using System.Security.Claims;
using FiveSafesTes.Core.Models.ViewModels;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Records a reviewer's decision on a federated task result, sent by the Egress app to EgressResults.
    /// It only records; the federated poll releases or rejects the result.
    /// </summary>
    public static class FederatedEgressDecision
    {
        public const string EgressAdminRole = "data-egress-admin";

        /// <summary>Null when the review is not federated (the native path then runs); otherwise an HTTP status.</summary>
        public static async Task<int?> TryHandleAsync(
            EgressReview review, ClaimsPrincipal user, IOutputApproval approval, CancellationToken ct)
        {
            if (!Guid.TryParse(review.SubId, out var guid))
                return null;

            // The role comes first, so a caller who may not decide cannot learn whether a task has a review.
            if (!user.IsInRole(EgressAdminRole))
                return 403;

            // Records are keyed by the canonical form, as the poll does.
            var taskId = guid.ToString("D");
            if (await approval.GetAsync(taskId, ct) == null)
                return 404;

            if (review.FileResults is not { Count: 1 }
                || string.IsNullOrWhiteSpace(review.ResultSha256)
                || string.IsNullOrWhiteSpace(review.Reviewer)
                || review.DecidedAt is not { } decidedAt)
                return 400;

            var utc = decidedAt.Kind == DateTimeKind.Local
                ? decidedAt.ToUniversalTime()
                : DateTime.SpecifyKind(decidedAt, DateTimeKind.Utc);

            var decision = new ReviewDecision(
                review.FileResults[0].Approved,
                review.Reviewer,
                new DateTimeOffset(utc, TimeSpan.Zero),
                review.Reason,
                review.ResultSha256);

            return await approval.RecordDecisionAsync(taskId, decision, ct) switch
            {
                DecisionOutcome.Recorded or DecisionOutcome.AlreadyRecorded => 200,
                DecisionOutcome.NotRegistered => 404,
                DecisionOutcome.ReasonRequired => 400,
                _ => 409
            };
        }
    }
}
