namespace Agent.Api.FederatedTasks
{
    /// <summary>Keeps the reviewer's decision on a task result, next to the result itself.</summary>
    public interface IOutputReviewStore
    {
        Task<OutputReview?> GetAsync(string taskId, CancellationToken ct);
        Task SaveAsync(string taskId, OutputReview review, CancellationToken ct);
    }
}
