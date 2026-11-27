using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Api.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class AcroPoolingTests
{
    private static string Table(string statistic, string[] rows, string[] columns, long[][] count, double[][]? sum = null,
        string method = "pivot_table")
    {
        var table = new JsonObject
        {
            ["method"] = method, ["statistic"] = statistic,
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()),
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode)c).ToArray()),
            ["count"] = JsonSerializer.SerializeToNode(count)
        };
        if (sum != null) table["sum"] = JsonSerializer.SerializeToNode(sum);
        return table.ToJsonString();
    }

    private static string Hospital(params string[] tables) => $"{{\"tables\":[{string.Join(",", tables)}]}}";

    private static string Evidence(params string?[] hospitals) => $"[{string.Join(",", hospitals.Select(h => h ?? "null"))}]";

    private static readonly string[] Sexes = ["F", "M"];
    private static readonly string[] Age = ["age"];

    private static readonly string[] ThreeSexes = ["F", "M", "X"];

    public static TheoryData<string, string, string, int> Cases() => new()
    {
        {
            "released",
            Evidence(Hospital(Table("count", Sexes, Age, [[67], [50]])), Hospital(Table("count", Sexes, Age, [[54], [41]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M"],"columns":["age"],"cells":[[121],[91]],"rowTotals":[121,91],"columnTotals":[212],"total":212}]""",
            0
        },
        {
            // The pooled n is released, so a table's only withheld cell would be n minus the released cells: the smallest
            // released cell (F 11) is withheld with it.
            "one withheld, so a second",
            Evidence(Hospital(Table("count", Sexes, Age, [[2], [1]])), Hospital(Table("count", Sexes, Age, [[9], [3]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M"],"columns":["age"],"cells":[[null],[null]],"rowTotals":[null,null],"columnTotals":[null],"total":null}]""",
            2
        },
        {
            "two withheld, none more",
            Evidence(Hospital(Table("count", ThreeSexes, Age, [[20], [1], [2]])), Hospital(Table("count", ThreeSexes, Age, [[15], [3], [1]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M","X"],"columns":["age"],"cells":[[35],[null],[null]],"rowTotals":[35,null,null],"columnTotals":[35],"total":35}]""",
            2
        },
        {
            // Sorted, not in the order the hospitals listed them ("east" last would show the first hospital had none). south
            // (5) is withheld, then east (15), the smallest released cell, so south is not n minus the rest.
            "labels differ",
            Evidence(Hospital(Table("count", ["north", "south"], ["x"], [[20], [3]], method: "crosstab")),
                Hospital(Table("count", ["east", "north", "south"], ["x"], [[15], [20], [2]], method: "crosstab"))),
            """[{"method":"crosstab","statistic":"count","rows":["east","north","south"],"columns":["x"],"cells":[[null],[40],[null]],"rowTotals":[null,40,null],"columnTotals":[40],"total":40}]""",
            2
        },
        {
            "numeric labels sort as numbers",
            Evidence(Hospital(Table("count", ["10", "9"], ["x"], [[20], [20]])), Hospital(Table("count", ["100"], ["x"], [[20]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["9","10","100"],"columns":["x"],"cells":[[20],[20],[20]],"rowTotals":[20,20,20],"columnTotals":[60],"total":60}]""",
            0
        },
        {
            "all withheld",
            Evidence(Hospital(Table("count", Sexes, Age, [[1], [2]])), Hospital(Table("count", Sexes, Age, [[3], [1]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M"],"columns":["age"],"cells":[[null],[null]],"rowTotals":[null,null],"columnTotals":[null],"total":null}]""",
            2
        },
        {
            "mean",
            Evidence(Hospital(Table("mean", ThreeSexes, Age, [[20], [1], [2]], [[600.0], [50.0], [80.0]])),
                Hospital(Table("mean", ThreeSexes, Age, [[15], [3], [1]], [[450.0], [90.0], [40.0]]))),
            """[{"method":"pivot_table","statistic":"mean","rows":["F","M","X"],"columns":["age"],"cells":[[30.0],[null],[null]]}]""",
            2
        },
        {
            "sum",
            Evidence(Hospital(Table("sum", ThreeSexes, Age, [[20], [1], [2]], [[600.0], [50.0], [80.0]])),
                Hospital(Table("sum", ThreeSexes, Age, [[15], [3], [1]], [[450.0], [90.0], [40.0]]))),
            """[{"method":"pivot_table","statistic":"sum","rows":["F","M","X"],"columns":["age"],"cells":[[1050.0],[null],[null]],"rowTotals":[1050.0,null,null],"columnTotals":[1050.0],"total":1050.0}]""",
            2
        },
        { "no evidence", Evidence(null, null), "[]", 0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Pools_and_withholds(string name, string evidence, string expectedTables, int withheld)
    {
        var pooled = AcroPooling.Pool(evidence, minimum: 10);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedTables), JsonNode.Parse(pooled.Tables.GetRawText())),
            $"{name}: {pooled.Tables.GetRawText()}");
        Assert.Equal(withheld, pooled.WithheldCells);
    }

    public static TheoryData<string> Mismatches() => new()
    {
        Evidence(Hospital(Table("count", Sexes, Age, [[20], [20]])), null),
        Evidence(Hospital(Table("count", Sexes, Age, [[20], [20]])), Hospital()),
        Evidence(Hospital(Table("count", Sexes, Age, [[20], [20]])), Hospital(Table("sum", Sexes, Age, [[20], [20]], [[1.0], [1.0]]))),
        Evidence(Hospital(Table("count", Sexes, Age, [[20], [20]])), Hospital(Table("count", Sexes, Age, [[20], [20]], method: "crosstab"))),
        // Two finite sums whose total is not a number JSON can hold.
        Evidence(Hospital(Table("sum", Sexes, Age, [[20], [20]], [[1e308], [1.0]])), Hospital(Table("sum", Sexes, Age, [[20], [20]], [[1e308], [1.0]]))),
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void Refuses_evidence_that_does_not_match(string evidence)
    {
        var e = Assert.Throws<AcroPoolingException>(() => AcroPooling.Pool(evidence, minimum: 10));
        Assert.DoesNotContain("\"F\"", e.Message);
    }
}
