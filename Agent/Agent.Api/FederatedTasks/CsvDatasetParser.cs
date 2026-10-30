using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Parses a CSV stream (header row required) into a <see cref="DatasetTable"/>.
    /// </summary>
    public static class CsvDatasetParser
    {
        public static DatasetReadResult Parse(Stream csv)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                IgnoreBlankLines = true,
            };

            using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
            using var parser = new CsvReader(reader, config);

            try
            {
                if (!parser.Read() || !parser.ReadHeader() || parser.HeaderRecord is not { Length: > 0 } header)
                {
                    return DatasetReadResult.Fail(FederatedTaskErrorCodes.DatasetNotFound, "dataset file is empty");
                }

                var rows = new List<IReadOnlyList<string?>>();
                while (parser.Read())
                {
                    // GetField, not TryGetField: TryGetField turns a malformed field into a silent null.
                    var row = new string?[header.Length];
                    for (var i = 0; i < header.Length; i++)
                    {
                        row[i] = i < parser.Parser.Count ? parser.GetField(i) : null;
                    }
                    rows.Add(row);
                }

                return DatasetReadResult.Ok(new DatasetTable(header, rows));
            }
            catch (CsvHelperException ex)
            {
                // Bad data is the uploader's to fix, not an infrastructure failure. The exception text quotes the
                // offending record, so only the line number leaves the TRE.
                var line = ex.Context?.Parser?.RawRow;
                return DatasetReadResult.Fail(FederatedTaskErrorCodes.InvalidDataset,
                    line is > 0 ? $"dataset file is not valid CSV near line {line}" : "dataset file is not valid CSV");
            }
        }
    }
}
