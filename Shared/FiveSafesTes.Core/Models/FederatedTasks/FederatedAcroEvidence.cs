using System.Text.Json;

namespace FiveSafesTes.Core.Models.FederatedTasks
{
    /// <summary>
    /// The ACRO evidence a hospital's result carries (<c>result.acro</c>, written by the runner for
    /// site_analysis(df, acro)): per table, its labels and the counts (and sums) behind each cell, which the aggregator
    /// pools and checks for small numbers. See runner/README.md, "ACRO tables".
    /// </summary>
    public static class FederatedAcroEvidence
    {
        public const int MaxTables = 64;
        public const int MaxLabels = 1000;

        public static readonly IReadOnlySet<string> Methods = new HashSet<string> { "crosstab", "pivot_table" };
        public static readonly IReadOnlySet<string> Statistics = new HashSet<string> { "count", "sum", "mean" };

        private static readonly HashSet<string> TableFields = ["method", "statistic", "rows", "columns", "count", "sum"];

        /// <summary>Null when the evidence has the expected shape, else a message naming the field (never its content).</summary>
        public static string? Validate(JsonElement acro)
        {
            const string root = "result.acro";
            if (acro.ValueKind != JsonValueKind.Object || acro.EnumerateObject().Any(p => p.Name != "tables"))
            {
                return $"field '{root}' must be an object with tables only";
            }

            if (!acro.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
            {
                return $"field '{root}.tables' must be an array";
            }

            if (tables.GetArrayLength() > MaxTables)
            {
                return $"field '{root}.tables' has more than {MaxTables} tables";
            }

            var i = 0;
            foreach (var table in tables.EnumerateArray())
            {
                if (Table(table, $"{root}.tables[{i++}]") is { } problem)
                {
                    return problem;
                }
            }

            return null;
        }

        private static string? Table(JsonElement table, string at)
        {
            if (table.ValueKind != JsonValueKind.Object || table.EnumerateObject().Any(p => !TableFields.Contains(p.Name)))
            {
                return $"field '{at}' must be an object with method, statistic, rows, columns, count and sum only";
            }

            if (!table.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || !Methods.Contains(method.GetString()!))
            {
                return $"field '{at}.method' must be crosstab or pivot_table";
            }

            if (!table.TryGetProperty("statistic", out var statistic) || statistic.ValueKind != JsonValueKind.String
                || !Statistics.Contains(statistic.GetString()!))
            {
                return $"field '{at}.statistic' must be count, sum or mean";
            }

            if (Labels(table, "rows", at) is { } rowsProblem) return rowsProblem;
            if (Labels(table, "columns", at) is { } columnsProblem) return columnsProblem;
            var (rows, columns) = (table.GetProperty("rows").GetArrayLength(), table.GetProperty("columns").GetArrayLength());

            if (Matrix(table, "count", at, rows, columns, wholeNonNegative: true) is { } countProblem) return countProblem;

            var hasSum = table.TryGetProperty("sum", out _);
            if (statistic.GetString() == "count")
            {
                return hasSum ? $"field '{at}.sum' is only for sum and mean tables" : null;
            }

            return hasSum
                ? Matrix(table, "sum", at, rows, columns, wholeNonNegative: false)
                : $"field '{at}.sum' is required for sum and mean tables";
        }

        private static string? Labels(JsonElement table, string name, string at)
        {
            if (!table.TryGetProperty(name, out var labels) || labels.ValueKind != JsonValueKind.Array
                || labels.GetArrayLength() is 0 or > MaxLabels
                || labels.EnumerateArray().Any(l => l.ValueKind != JsonValueKind.String || l.GetString()!.Length > FederatedResultLimits.MaxStringLength))
            {
                return $"field '{at}.{name}' must be 1 to {MaxLabels} labels of at most {FederatedResultLimits.MaxStringLength} characters";
            }

            return null;
        }

        private static string? Matrix(JsonElement table, string name, string at, int rows, int columns, bool wholeNonNegative)
        {
            var message = wholeNonNegative
                ? $"field '{at}.{name}' must be a rows-by-columns array of non-negative integers"
                : $"field '{at}.{name}' must be a rows-by-columns array of numbers";
            if (!table.TryGetProperty(name, out var matrix) || matrix.ValueKind != JsonValueKind.Array || matrix.GetArrayLength() != rows)
            {
                return message;
            }

            foreach (var row in matrix.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columns)
                {
                    return message;
                }

                foreach (var cell in row.EnumerateArray())
                {
                    var ok = cell.ValueKind == JsonValueKind.Number
                        && (wholeNonNegative ? cell.TryGetInt64(out var c) && c >= 0 : cell.TryGetDouble(out var d) && double.IsFinite(d));
                    if (!ok)
                    {
                        return message;
                    }
                }
            }

            return null;
        }
    }
}
