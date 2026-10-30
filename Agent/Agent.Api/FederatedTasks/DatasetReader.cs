using System.Security.Cryptography;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Resolves a dataset name to its single data file under {DatasetPrefix}{name}/ and parses it.
    /// </summary>
    public class DatasetReader : IDatasetReader
    {
        // The only format the reader parses. Upload policy extensions are separate: see FederatedTaskSettings.
        private const string DatasetExtension = ".csv";

        private readonly IDatasetStore _store;
        private readonly FederatedTaskSettings _settings;

        public DatasetReader(IDatasetStore store, FederatedTaskSettings settings)
        {
            _store = store;
            _settings = settings;
        }

        public async Task<DatasetReadResult> ReadAsync(string datasetName, CancellationToken ct)
        {
            // Validate before touching the store so the name can never escape the dataset prefix.
            if (!FederatedTaskNames.IsValidDatasetName(datasetName))
            {
                return DatasetReadResult.Fail(FederatedTaskErrorCodes.InvalidDatasetName,
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
                return DatasetReadResult.Fail(FederatedTaskErrorCodes.DatasetNotFound,
                    $"no dataset file found for '{datasetName}'");
            }

            if (keys.Count > 1)
            {
                return DatasetReadResult.Fail(FederatedTaskErrorCodes.AmbiguousDataset,
                    $"more than one dataset file found for '{datasetName}'");
            }

            await using var stream = await _store.OpenReadAsync(keys[0], ct);

            // Hash the exact bytes once; the parser then reads the same buffer.
            var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, ct);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length))).ToLowerInvariant();
            bytes.Position = 0;

            // The hash is kept on failures too: the file was read, so the report must say which file it was.
            return CsvDatasetParser.Parse(bytes) with { Sha256 = sha256 };
        }
    }
}
