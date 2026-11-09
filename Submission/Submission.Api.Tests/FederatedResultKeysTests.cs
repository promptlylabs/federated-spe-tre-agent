using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests;

public class FederatedResultKeysTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void For_builds_tre_id_task_sha_key()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal($"7/11111111-2222-3333-4444-555555555555/{Sha}.json", FederatedResultKeys.For(7, id, Sha));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]
    public void For_rejects_non_hex_sha(string sha)
    {
        Assert.Throws<ArgumentException>(() => FederatedResultKeys.For(1, Guid.NewGuid(), sha));
    }
}
