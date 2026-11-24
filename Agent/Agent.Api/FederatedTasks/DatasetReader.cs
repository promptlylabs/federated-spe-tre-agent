using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Resolves a dataset name to its single data file under {DatasetPrefix}{name}/ and its schema.json.
    /// </summary>
    public class DatasetReader : IDatasetReader
    {
        // Not AllowedExtensions: that is the upload policy, and the runner reads only CSV.
        private const string DatasetExtension = ".csv";
        private const string SchemaFileName = "schema.json";

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
            IReadOnlyList<string> listed;
            try
            {
                listed = await _store.ListKeysAsync(prefix, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DatasetStoreException($"Listing dataset '{datasetName}' failed", ex);
            }

            var children = listed
                .Where(k => k.IndexOf('/', prefix.Length) < 0) // direct children only
                .ToList();
            var keys = children
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

            var schemaKey = $"{prefix}{SchemaFileName}";
            return DatasetResolveResult.Ok(keys[0], children.Contains(schemaKey, StringComparer.Ordinal) ? schemaKey : null);
        }
    }
}
