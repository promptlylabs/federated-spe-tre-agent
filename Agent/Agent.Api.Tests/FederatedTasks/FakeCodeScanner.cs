using Agent.Api.FederatedTasks;

namespace Agent.Api.Tests.FederatedTasks;

/// <summary>A scanner that returns <see cref="Result"/> (Clean by default) and counts its calls.</summary>
public sealed class FakeCodeScanner : ICodeScanner
{
    public const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public CodeScanResult Result { get; set; } = new(CodeScanKind.Clean, [], "test", Sha, null);

    public int Calls { get; private set; }

    public Task<CodeScanResult> ScanAsync(string code, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
