using System.Text.Json;
using Agent.Api.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class OutputReadPolicyTests
{
    private static List<JsonElement> Statements(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Statement").EnumerateArray().Select(s => s.Clone()).ToList();
    }

    private static string[] Strings(JsonElement statement, string property) =>
        statement.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public void OutputReadPolicy_allows_only_list_and_get_on_output_bucket()
    {
        var statements = Statements(OutputReadPolicy.Build("federated-outputs"));

        Assert.Equal(3, statements.Count);
        Assert.All(statements, s => Assert.Equal("Allow", s.GetProperty("Effect").GetString()));

        var actions = statements.SelectMany(s => Strings(s, "Action")).ToArray();
        Assert.Equal(
            new[] { "s3:GetBucketLocation", "s3:GetBucketPolicyStatus", "s3:GetObject", "s3:ListAllMyBuckets", "s3:ListBucket" },
            actions.OrderBy(a => a, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(actions, a => a.Contains("Put") || a.Contains("Delete") || a.Contains('*'));

        Assert.Equal(["arn:aws:s3:::*"], Strings(statements[0], "Resource"));
        Assert.Equal(["s3:ListBucket"], Strings(statements[1], "Action"));
        Assert.Equal(["arn:aws:s3:::federated-outputs"], Strings(statements[1], "Resource"));
        Assert.Equal(["s3:GetObject"], Strings(statements[2], "Action"));
        Assert.Equal(["arn:aws:s3:::federated-outputs/*"], Strings(statements[2], "Resource"));
    }

    [Fact]
    public void DatasetUploadPolicy_grants_nothing_on_output_bucket()
    {
        var statements = Statements(DatasetUploadPolicy.Build("federated-datasets", "dataset/", ["csv"]));

        var resources = statements.SelectMany(s => Strings(s, "Resource"));
        Assert.DoesNotContain(resources, r => r.Contains("federated-outputs"));
    }
}
