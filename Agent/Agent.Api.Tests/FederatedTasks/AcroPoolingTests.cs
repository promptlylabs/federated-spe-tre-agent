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

    public static TheoryData<string, string, string, int> Cases() => new()
    {
        {
            "released",
            Evidence(Hospital(Table("count", Sexes, Age, [[67], [50]])), Hospital(Table("count", Sexes, Age, [[54], [41]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M"],"columns":["age"],"cells":[[121],[91]],"rowTotals":[121,91],"columnTotals":[212],"total":212}]""",
            0
        },
        {
            "withheld",
            Evidence(Hospital(Table("count", Sexes, Age, [[2], [1]])), Hospital(Table("count", Sexes, Age, [[9], [3]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M"],"columns":["age"],"cells":[[11],[null]],"rowTotals":[11,null],"columnTotals":[11],"total":11}]""",
            1
        },
        {
            "labels differ",
            Evidence(Hospital(Table("count", ["north", "south"], ["x"], [[20], [3]], method: "crosstab")),
                Hospital(Table("count", ["north", "east"], ["x"], [[20], [15]], method: "crosstab"))),
            """[{"method":"crosstab","statistic":"count","rows":["north","south","east"],"columns":["x"],"cells":[[40],[null],[15]],"rowTotals":[40,null,15],"columnTotals":[55],"total":55}]""",
            1
        },
        {
            "all withheld",
            Evidence(Hospital(Table("count", Sexes, Age, [[1], [2]])), Hospital(Table("count", Sexes, Age, [[3], [1]]))),
            """[{"method":"pivot_table","statistic":"count","rows":["F","M"],"columns":["age"],"cells":[[null],[null]],"rowTotals":[null,null],"columnTotals":[null],"total":null}]""",
            2
        },
        {
            "mean",
            Evidence(Hospital(Table("mean", Sexes, Age, [[2], [1]], [[60.0], [50.0]])), Hospital(Table("mean", Sexes, Age, [[9], [3]], [[270.0], [90.0]]))),
            """[{"method":"pivot_table","statistic":"mean","rows":["F","M"],"columns":["age"],"cells":[[30.0],[null]]}]""",
            1
        },
        {
            "sum",
            Evidence(Hospital(Table("sum", Sexes, Age, [[2], [1]], [[60.0], [50.0]])), Hospital(Table("sum", Sexes, Age, [[9], [3]], [[270.0], [90.0]]))),
            """[{"method":"pivot_table","statistic":"sum","rows":["F","M"],"columns":["age"],"cells":[[330.0],[null]],"rowTotals":[330.0,null],"columnTotals":[330.0],"total":330.0}]""",
            1
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
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void Refuses_evidence_that_does_not_match(string evidence)
    {
        var e = Assert.Throws<AcroPoolingException>(() => AcroPooling.Pool(evidence, minimum: 10));
        Assert.DoesNotContain("\"F\"", e.Message);
    }
}
