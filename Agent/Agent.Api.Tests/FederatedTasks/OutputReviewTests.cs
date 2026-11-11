using System.Text;
using System.Text.Json;
using Agent.Api.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class OutputReviewTests
{
    private const string Sha = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";

    private static OutputReview Rejected() => new(
        Sha,
        new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero),
        true,
        DisclosureCheckStatus.Failed,
        "reviewer@hospital.example",
        new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero),
        "Small cell counts");

    [Fact]
    public void Json_round_trips_a_rejected_review()
    {
        var review = Rejected();

        var back = OutputReviewJson.Deserialize(OutputReviewJson.Serialize(review));

        Assert.Equal(review, back);
        Assert.True(back.IsRejected);
    }

    [Fact]
    public void Json_is_a_5s_crate_disclosure_check()
    {
        using var doc = JsonDocument.Parse(OutputReviewJson.Serialize(Rejected()));
        var root = doc.RootElement;

        Assert.Equal("AssessAction", root.GetProperty("@type").GetString());
        Assert.Equal("https://w3id.org/shp#DisclosureCheck", root.GetProperty("additionalType").GetString());
        Assert.Equal("http://schema.org/FailedActionStatus", root.GetProperty("actionStatus").GetString());
        Assert.Equal("Small cell counts", root.GetProperty("description").GetString());
        Assert.Equal(Sha, root.GetProperty("object").GetProperty("sha256").GetString());
    }

    [Fact]
    public void Pending_is_neither_approved_nor_rejected()
    {
        var review = OutputReview.Pending(Sha, DateTimeOffset.UtcNow);

        Assert.True(review.IsPending);
        Assert.False(review.IsApproved);
        Assert.False(review.IsRejected);
        Assert.False(review.Registered);
    }

    [Fact]
    public void Deserialize_rejects_an_unknown_action_status()
    {
        var json = Encoding.UTF8.GetString(OutputReviewJson.Serialize(Rejected()))
            .Replace("FailedActionStatus", "BogusActionStatus");

        Assert.ThrowsAny<Exception>(() => OutputReviewJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_rejects_a_missing_sha()
    {
        var json = """{"@type":"AssessAction","actionStatus":"http://schema.org/CompletedActionStatus","startTime":"2026-11-11T09:00:00+00:00","registered":true}""";

        Assert.ThrowsAny<Exception>(() => OutputReviewJson.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
}
