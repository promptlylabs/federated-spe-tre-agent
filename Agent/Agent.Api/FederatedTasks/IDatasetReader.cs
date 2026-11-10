namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Finds a dataset by name, then reads it. Finding it reads no data values, so its errors are input-check errors.
    /// </summary>
    public interface IDatasetReader
    {
        Task<DatasetResolveResult> ResolveAsync(string datasetName, CancellationToken ct);

        Task<DatasetReadResult> ReadAsync(string fileKey, CancellationToken ct);
    }
}
