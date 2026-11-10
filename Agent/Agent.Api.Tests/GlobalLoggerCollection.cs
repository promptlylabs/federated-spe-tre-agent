using Xunit;

namespace Agent.Api.Tests;

/// <summary>
/// Tests that swap Serilog's global Log.Logger. xUnit runs this collection alone, after the parallel ones, so no other
/// test writes into a swapped-in logger while it is installed.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalLoggerCollection
{
    public const string Name = "Global Serilog logger";
}
