namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// The dataset bucket failed while a file was opened or read. The Agent retries these; any other exception from
    /// the reader is a fault in the TRE and is reported at once.
    /// </summary>
    public sealed class DatasetStoreException(string message, Exception inner) : Exception(message, inner);
}
