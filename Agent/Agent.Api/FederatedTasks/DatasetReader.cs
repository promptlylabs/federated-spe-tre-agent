using System.Security.Cryptography;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Resolves a dataset name to its single data file under {DatasetPrefix}{name}/, then reads and parses that file.
    /// </summary>
    public class DatasetReader : IDatasetReader
    {
        // Not AllowedExtensions: that is the upload policy, and the parser reads only CSV.
        private const string DatasetExtension = ".csv";

        private readonly IDatasetStore _store;
        private readonly FederatedTaskSettings _settings;

        public DatasetReader(IDatasetStore store, FederatedTaskSettings settings)
        {
            _store = store;
            _settings = settings;
        }

        public async Task<DatasetResolveResult> ResolveAsync(string datasetName, CancellationToken ct)
        {
            // Validate before touching the store so the name can never escape the dataset prefix.
            if (!FederatedTaskNames.IsValidDatasetName(datasetName))
            {
                return DatasetResolveResult.Fail(FederatedTaskErrorCodes.InvalidDatasetName,
                    "dataset name must match ^[a-z0-9][a-z0-9_-]{0,62}$");
            }

            // Trailing slash so "cohort" does not match "cohort_2024/...".
            var prefix = $"{_settings.DatasetPrefix}{datasetName}/";
            var keys = (await _store.ListKeysAsync(prefix, ct))
                .Where(k => k.IndexOf('/', prefix.Length) < 0) // direct children only
                .Where(k => k.EndsWith(DatasetExtension, StringComparison.Ordinal))
                .ToList();

            if (keys.Count == 0)
            {
                return DatasetResolveResult.Fail(FederatedTaskErrorCodes.DatasetNotFound,
                    $"no dataset file found for '{datasetName}'");
            }

            if (keys.Count > 1)
            {
                return DatasetResolveResult.Fail(FederatedTaskErrorCodes.AmbiguousDataset,
                    $"more than one dataset file found for '{datasetName}'");
            }

            return DatasetResolveResult.Ok(keys[0]);
        }

        public async Task<DatasetReadResult> ReadAsync(string fileKey, CancellationToken ct)
        {
            // Only the store calls are wrapped, so the Agent can retry a bucket failure and still report a parser
            // fault at once. If the file was removed after ResolveAsync, this is a store failure: the task is retried
            // and then fails with internal_error, not dataset_not_found. Datasets are not removed while tasks run.
            Stream stream;
            try
            {
                stream = await _store.OpenReadAsync(fileKey, ct);
            }
            catch (Exception ex)
            {
                throw StoreFailed(fileKey, ex);
            }

            await using (stream)
            {
                string sha256;
                try
                {
                    sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    throw StoreFailed(fileKey, ex);
                }
                stream.Position = 0;

                // The hash is kept on failures too: the file was read, so the report must say which file it was.
                return CsvDatasetParser.Parse(stream) with { Sha256 = sha256 };
            }
        }

        private static DatasetStoreException StoreFailed(string fileKey, Exception ex) =>
            new($"Reading dataset file '{fileKey}' failed", ex);
    }
}
