using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Api.FederatedTasks
{
    /// <summary>The pooled ACRO tables and how many of their cells were withheld.</summary>
    public sealed record AcroPoolingResult(JsonElement Tables, int WithheldCells);

    /// <summary>The hospitals' ACRO evidence cannot be pooled (it does not match). The message names positions only.</summary>
    public sealed class AcroPoolingException(string message) : Exception(message);

    /// <summary>
    /// Pools the hospitals' ACRO evidence (see FederatedAcroEvidence) in the aggregator's own process, with no researcher
    /// code: for each table, the union of the hospitals' labels, sorted (a label missing at a hospital counts 0 there), the summed
    /// counts and sums, and means as pooled sum over pooled count. A cell whose pooled count is below the minimum is
    /// withheld (null), with its statistic; a table never keeps exactly one withheld cell (secondary suppression: the smallest
    /// released cell is withheld too). Count and sum tables get row, column and grand totals over the released cells
    /// only (null when none is released), as ACRO recalculates margins; mean tables get none.
    /// </summary>
    public static class AcroPooling
    {
        public static AcroPoolingResult Pool(string evidenceJson, int minimum)
        {
            using var parsed = JsonDocument.Parse(evidenceJson);
            var hospitals = parsed.RootElement.EnumerateArray().ToList();
            var tables = new JsonArray();
            var withheld = 0;
            if (hospitals.All(h => h.ValueKind == JsonValueKind.Null))
            {
                return new AcroPoolingResult(ToElement(tables), 0);
            }

            if (hospitals.Any(h => h.ValueKind == JsonValueKind.Null))
            {
                throw new AcroPoolingException("some hospitals' results have ACRO tables and some do not");
            }

            var perHospital = hospitals.Select(h => h.GetProperty("tables").EnumerateArray().ToList()).ToList();
            if (perHospital.Select(t => t.Count).Distinct().Count() != 1)
            {
                throw new AcroPoolingException("the hospitals' results have different numbers of ACRO tables");
            }

            for (var t = 0; t < perHospital[0].Count; t++)
            {
                var (table, cellsWithheld) = PoolTable(perHospital.Select(h => h[t]).ToList(), t, minimum);
                tables.Add(table);
                withheld += cellsWithheld;
            }

            return new AcroPoolingResult(ToElement(tables), withheld);
        }

        private static (JsonObject Table, int Withheld) PoolTable(List<JsonElement> parts, int position, int minimum)
        {
            var method = parts[0].GetProperty("method").GetString()!;
            var statistic = parts[0].GetProperty("statistic").GetString()!;
            if (parts.Any(p => p.GetProperty("method").GetString() != method || p.GetProperty("statistic").GetString() != statistic))
            {
                throw new AcroPoolingException($"ACRO table {position + 1} is not the same kind of table at every hospital");
            }

            var rows = Union(parts, "rows");
            var columns = Union(parts, "columns");
            var counts = new long[rows.Count, columns.Count];
            var sums = new double[rows.Count, columns.Count];
            foreach (var part in parts)
            {
                var partRows = Labels(part, "rows");
                var partColumns = Labels(part, "columns");
                var count = part.GetProperty("count");
                var sum = statistic == "count" ? (JsonElement?)null : part.GetProperty("sum");
                for (var i = 0; i < partRows.Count; i++)
                {
                    for (var j = 0; j < partColumns.Count; j++)
                    {
                        var (r, c) = (rows.IndexOf(partRows[i]), columns.IndexOf(partColumns[j]));
                        counts[r, c] += count[i][j].GetInt64();
                        if (sum is { } s)
                        {
                            sums[r, c] += s[i][j].GetDouble();
                        }
                    }
                }
            }

            if (sums.Cast<double>().Any(v => !double.IsFinite(v)))
            {
                throw new AcroPoolingException($"ACRO table {position + 1} has sums too large to add up");
            }

            var withheldCells = new bool[rows.Count, columns.Count];
            for (var r = 0; r < rows.Count; r++)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    withheldCells[r, c] = counts[r, c] < minimum;
                }
            }

            // Secondary suppression: the pooled n is released, so a table with exactly one withheld cell would give it away
            // (n minus the released cells, when the table covers everyone). The smallest released cell is withheld with it.
            if (withheldCells.Cast<bool>().Count(w => w) == 1)
            {
                var smallest = Enumerable.Range(0, rows.Count).SelectMany(r => Enumerable.Range(0, columns.Count).Select(c => (r, c)))
                    .Where(rc => !withheldCells[rc.r, rc.c])
                    .OrderBy(rc => counts[rc.r, rc.c]).ThenBy(rc => rc.r).ThenBy(rc => rc.c)
                    .Cast<(int r, int c)?>().FirstOrDefault();
                if (smallest is { } cell)
                {
                    withheldCells[cell.r, cell.c] = true;
                }
            }

            var withheld = 0;
            var cells = new double?[rows.Count, columns.Count];
            for (var r = 0; r < rows.Count; r++)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    if (withheldCells[r, c])
                    {
                        withheld++;
                        continue;
                    }

                    cells[r, c] = statistic switch
                    {
                        "count" => counts[r, c],
                        "sum" => sums[r, c],
                        _ => sums[r, c] / counts[r, c]
                    };
                }
            }

            var table = new JsonObject
            {
                ["method"] = method, ["statistic"] = statistic,
                ["rows"] = new JsonArray(rows.Select(l => (JsonNode)l).ToArray()),
                ["columns"] = new JsonArray(columns.Select(l => (JsonNode)l).ToArray()),
                ["cells"] = new JsonArray(Enumerable.Range(0, rows.Count)
                    .Select(r => (JsonNode)new JsonArray(Enumerable.Range(0, columns.Count).Select(c => Number(cells[r, c], statistic)).ToArray()))
                    .ToArray())
            };
            if (statistic != "mean")
            {
                table["rowTotals"] = new JsonArray(Enumerable.Range(0, rows.Count)
                    .Select(r => Number(Total(Enumerable.Range(0, columns.Count).Select(c => cells[r, c])), statistic)).ToArray());
                table["columnTotals"] = new JsonArray(Enumerable.Range(0, columns.Count)
                    .Select(c => Number(Total(Enumerable.Range(0, rows.Count).Select(r => cells[r, c])), statistic)).ToArray());
                table["total"] = Number(Total(cells.Cast<double?>()), statistic);
            }

            return (table, withheld);
        }

        /// <summary>The sum of the released cells, or null when none is released.</summary>
        private static double? Total(IEnumerable<double?> cells)
        {
            var released = cells.Where(c => c.HasValue).Select(c => c!.Value).ToList();
            return released.Count == 0 ? null : released.Sum();
        }

        /// <summary>Counts are written as integers; sums and means as numbers.</summary>
        private static JsonNode? Number(double? value, string statistic) =>
            value is not { } v ? null : statistic == "count" ? JsonValue.Create((long)v) : JsonValue.Create(v);

        private static List<string> Labels(JsonElement table, string name) =>
            table.GetProperty(name).EnumerateArray().Select(l => l.GetString()!).ToList();

        /// <summary>
        /// The labels of all hospitals, each once, sorted: as numbers when every label is one, else ordinally. Never in the
        /// order the hospitals listed them, which would show which hospital had a category.
        /// </summary>
        private static List<string> Union(List<JsonElement> parts, string name)
        {
            var labels = parts.SelectMany(p => Labels(p, name)).Distinct(StringComparer.Ordinal).ToList();
            var numeric = labels.All(l => double.TryParse(l, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
            return numeric
                ? labels.OrderBy(l => double.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ThenBy(l => l, StringComparer.Ordinal).ToList()
                : labels.OrderBy(l => l, StringComparer.Ordinal).ToList();
        }

        private static JsonElement ToElement(JsonArray tables) => JsonSerializer.SerializeToElement(tables);
    }
}
