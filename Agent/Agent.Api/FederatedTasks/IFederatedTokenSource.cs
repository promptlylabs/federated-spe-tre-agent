namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Gives the bearer token of the TRE service account for calls to Submission.
    /// </summary>
    public interface IFederatedTokenSource
    {
        Task<string> GetTokenAsync();
    }
}
