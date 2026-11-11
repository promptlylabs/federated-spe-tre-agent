namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Output approval through the Data Egress app. The review store has no compare-and-swap, so the
    /// order of writes matters: the pending record is saved before Egress is called, and after Egress
    /// accepts, the record is re-read and marked registered only if no decision arrived meanwhile.
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

        public async Task<OutputReview> RegisterAsync(EgressRegistration r, CancellationToken ct)
        {
            var existing = await _store.GetAsync(r.TaskId, ct);
            if (existing is { IsPending: false })
                return existing;

            if (existing is null)
                await _store.SaveAsync(r.TaskId, OutputReview.Pending(r.ResultSha256, _clock.GetUtcNow()), ct);

            await _egress.RegisterAsync(r, ct);

            var current = await _store.GetAsync(r.TaskId, ct)
                ?? throw new InvalidOperationException($"Review record for task {r.TaskId} disappeared during registration.");
            if (!current.IsPending || current.Registered)
                return current;

            var registered = current with { Registered = true };
            await _store.SaveAsync(r.TaskId, registered, ct);
            return registered;
        }

        public async Task<DecisionOutcome> RecordDecisionAsync(string taskId, ReviewDecision d, CancellationToken ct)
        {
            var current = await _store.GetAsync(taskId, ct);
            if (current is null)
                return DecisionOutcome.NotRegistered;

            if (!string.Equals(current.ResultSha256, d.ResultSha256, StringComparison.Ordinal))
                return DecisionOutcome.ShaMismatch;

            var reason = string.IsNullOrWhiteSpace(d.Reason) ? null : d.Reason;
            if (!d.Approved && reason is null)
                return DecisionOutcome.ReasonRequired;

            if (!current.IsPending)
                return current.IsApproved == d.Approved
                    ? DecisionOutcome.AlreadyRecorded
                    : DecisionOutcome.ConflictingDecision;

            var decided = current with
            {
                Registered = true,
                ActionStatus = d.Approved ? DisclosureCheckStatus.Completed : DisclosureCheckStatus.Failed,
                Agent = d.Reviewer,
                EndTime = d.DecidedAt,
                Description = reason
            };
            await _store.SaveAsync(taskId, decided, ct);
            return DecisionOutcome.Recorded;
        }
    }
}
