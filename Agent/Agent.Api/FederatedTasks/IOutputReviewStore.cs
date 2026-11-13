namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Keeps the reviewer's decision on a task result, next to the result itself, and a separate marker that says
    /// the Egress app has accepted the registration. The two are different objects, so the poll (which writes the
    /// marker) and the Egress callback (which writes the decision) never write the same object.
    /// </summary>
    public interface IOutputReviewStore
    {
        Task<OutputReview?> GetAsync(string taskId, CancellationToken ct);
        Task SaveAsync(string taskId, OutputReview review, CancellationToken ct);
        Task<bool> IsRegisteredAsync(string taskId, CancellationToken ct);
        Task MarkRegisteredAsync(string taskId, CancellationToken ct);
        Task<bool> IsWithdrawnAsync(string taskId, CancellationToken ct);

        /// <summary>Marks a pending review as withdrawn: the task failed before a decision, so none is accepted.</summary>
        Task MarkWithdrawnAsync(string taskId, CancellationToken ct);
    }
}
