namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Output approval through the Data Egress app. The review store has no compare-and-swap, so each object has
    /// one writer at a time: the review record is written by the poll only before Egress knows the task (the
    /// pending record), and after that only by the Egress callback (the decision). The poll records that Egress
    /// accepted the registration in a separate marker, so it can never overwrite a decision.
    /// </summary>
    public class EgressOutputApproval : IOutputApproval
    {
        private readonly IOutputReviewStore _store;
        private readonly IEgressReviewClient _egress;
        private readonly TimeProvider _clock;

        public EgressOutputApproval(IOutputReviewStore store, IEgressReviewClient egress, TimeProvider clock)
        {
            _store = store;
            _egress = egress;
            _clock = clock;
        }

        public Task<OutputReview?> GetAsync(string taskId, CancellationToken ct) => _store.GetAsync(taskId, ct);

        public Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct) => _store.IsRegisteredAsync(taskId, ct);

        public async Task<OutputReview> RegisterAsync(EgressRegistration r, CancellationToken ct)
        {
            var existing = await _store.GetAsync(r.TaskId, ct);
            if (existing is { IsPending: false })
                return existing;

            // The pending record exists before Egress knows the task, so a decision always finds it.
            if (existing is null)
                await _store.SaveAsync(r.TaskId, OutputReview.Pending(r.ResultSha256, _clock.GetUtcNow()), ct);

            await _egress.RegisterAsync(r, ct);

            // From here on the review record belongs to the Egress callback; only the marker is written.
            await _store.MarkRegisteredAsync(r.TaskId, ct);

            return await _store.GetAsync(r.TaskId, ct)
                ?? throw new InvalidOperationException($"Review record for task {r.TaskId} disappeared during registration.");
        }

        public async Task<DecisionOutcome> RecordDecisionAsync(string taskId, ReviewDecision d, CancellationToken ct)
        {
            var current = await _store.GetAsync(taskId, ct);
            if (current is null)
                return DecisionOutcome.NotRegistered;

            if (!string.Equals(current.ResultSha256, d.ResultSha256, StringComparison.Ordinal))
                return DecisionOutcome.ShaMismatch;

            if (current.IsPending && await _store.IsWithdrawnAsync(taskId, ct))
                return DecisionOutcome.Withdrawn;

            var reason = string.IsNullOrWhiteSpace(d.Reason) ? null : d.Reason;
            if (!d.Approved && reason is null)
                return DecisionOutcome.ReasonRequired;

            if (!current.IsPending)
                return current.IsApproved == d.Approved
                    ? DecisionOutcome.AlreadyRecorded
                    : DecisionOutcome.ConflictingDecision;

            var decided = current with
            {
                ActionStatus = d.Approved ? DisclosureCheckStatus.Completed : DisclosureCheckStatus.Failed,
                Agent = d.Reviewer,
                EndTime = d.DecidedAt,
                Description = reason
            };
            await _store.SaveAsync(taskId, decided, ct);
            return DecisionOutcome.Recorded;
        }

        public async Task WithdrawAsync(string taskId, string reason, CancellationToken ct)
        {
            await _store.MarkWithdrawnAsync(taskId, ct);
            try
            {
                await _egress.WithdrawAsync(taskId, reason, ct);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Federated tasks: the Egress request for task {TaskId} could not be closed; a decision on it is refused", taskId);
            }
        }
    }
}
