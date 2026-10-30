using System.Globalization;
using FiveSafesTes.Core.Models.FederatedTasks;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Runs the allow-listed federated operations (count, mean) over an in-memory dataset.
    /// </summary>
    public static class FederatedOperationExecutor
    {
        public static OperationResult Execute(FederatedTaskSpec spec, DatasetTable table)
        {
            var columns = table.Columns.ToList();

            // Resolve each filter's column and parse its value once, not once per row.
            var filters = new List<ResolvedFilter>(spec.Filters.Count);
            foreach (var filter in spec.Filters)
            {
                var index = columns.IndexOf(filter.Column);
                if (index < 0)
                    return Fail(FederatedTaskErrorCodes.ColumnNotFound, $"column '{filter.Column}' not found");
                var isNumber = TryParse(filter.Value, out var number);
                filters.Add(new ResolvedFilter(index, filter.Op, filter.Value, isNumber, number));
            }

            var columnIndex = -1;
            if (spec.Column != null)
            {
                columnIndex = columns.IndexOf(spec.Column);
                if (columnIndex < 0)
                    return Fail(FederatedTaskErrorCodes.ColumnNotFound, $"column '{spec.Column}' not found");
            }

            var matching = new List<IReadOnlyList<string?>>();
            foreach (var row in table.Rows)
            {
                if (MatchesAll(row, filters))
                    matching.Add(row);
            }

            switch (spec.Operation)
            {
                case "count":
                    // With a column, rows whose value is empty are not counted.
                    var count = columnIndex < 0
                        ? matching.Count
                        : matching.Count(row => !string.IsNullOrWhiteSpace(Cell(row, columnIndex)));
                    return new OperationResult(count, count, null, null);

                case "mean":
                    if (columnIndex < 0)
                        return Fail(FederatedTaskErrorCodes.InvalidTask, "operation 'mean' requires a column");
                    return Mean(matching, columnIndex, spec.Column!);

                default:
                    return Fail(FederatedTaskErrorCodes.UnknownOperation, $"unknown operation '{spec.Operation}'");
            }
        }

        // Empty values are skipped; RowCount is the number of values averaged.
        private static OperationResult Mean(List<IReadOnlyList<string?>> rows, int columnIndex, string column)
        {
            decimal sum = 0;
            var count = 0;
            foreach (var row in rows)
            {
                var raw = Cell(row, columnIndex);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                if (!TryParse(raw, out var number))
                    return Fail(FederatedTaskErrorCodes.NonNumericValue, $"column '{column}' contains a non-numeric value");
                sum += number;
                count++;
            }

            return count == 0
                ? new OperationResult(null, 0, null, null)
                : new OperationResult(sum / count, count, null, null);
        }

        private static bool MatchesAll(IReadOnlyList<string?> row, List<ResolvedFilter> filters)
        {
            for (var i = 0; i < filters.Count; i++)
            {
                if (!Matches(Cell(row, filters[i].Index), filters[i]))
                    return false;
            }
            return true;
        }

        private static bool Matches(string? cell, ResolvedFilter filter)
        {
            var left = cell ?? string.Empty;
            var leftIsNumber = TryParse(left, out var leftNumber);

            if (filter.Op == "eq")
                return leftIsNumber && filter.IsNumber
                    ? leftNumber == filter.Number
                    : string.Equals(left, filter.Value, StringComparison.Ordinal);

            if (!leftIsNumber || !filter.IsNumber)
                return false;

            return filter.Op switch
            {
                "gt" => leftNumber > filter.Number,
                "lt" => leftNumber < filter.Number,
                "gte" => leftNumber >= filter.Number,
                "lte" => leftNumber <= filter.Number,
                _ => false
            };
        }

        private static string? Cell(IReadOnlyList<string?> row, int index) =>
            index < row.Count ? row[index] : null;

        private static bool TryParse(string value, out decimal number) =>
            decimal.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);

        private static OperationResult Fail(string code, string message) => new(null, 0, code, message);

        private readonly record struct ResolvedFilter(int Index, string Op, string Value, bool IsNumber, decimal Number);
    }
}
