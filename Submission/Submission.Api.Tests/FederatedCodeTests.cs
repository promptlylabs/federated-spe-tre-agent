using FiveSafesTes.Core.Models.FederatedTasks;
using Xunit;

namespace Submission.Api.Tests;

/// <summary>The aggregate() the Egress review page shows on its own: only when the code defines it once, plainly.</summary>
public class FederatedCodeTests
{
    private const string Site = "def site_analysis(df):\n    return {'n': len(df)}\n\n\n";
    private const string Aggregate = "def aggregate(results):\n    # pooled\n\n    return {'n': sum(r['n'] for r in results)}";

    [Theory]
    [InlineData(Site + Aggregate + "\n", Aggregate)]
    [InlineData(Site + Aggregate + "\n\n\ndef helper():\n    pass\n", Aggregate)]
    [InlineData(Site, null)]
    // Python uses the last definition: a harmless first one must not be shown as the function that runs.
    [InlineData(Site + Aggregate + "\n\n\ndef aggregate(results):\n    return results[0]\n", null)]
    [InlineData(Site + Aggregate + "\n\n\naggregate = lambda results: results[0]\n", null)]
    [InlineData(Site + Aggregate + "\n\n\nfrom other import aggregate\n", null)]
    [InlineData(Site + Aggregate + "\n\n\nglobals()['aggregate'] = print\n", null)]
    public void AggregateFunction_only_when_defined_once(string code, string? expected)
    {
        Assert.Equal(expected, FederatedCode.AggregateFunction(code));
    }
}
