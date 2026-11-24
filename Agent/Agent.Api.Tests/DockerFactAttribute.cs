using System.Net;
using Agent.Api.Models;
using Docker.DotNet;
using Xunit;

namespace Agent.Api.Tests;

/// <summary>
/// A test that needs Docker and the runner image. It is skipped when the Docker socket cannot be reached or the image
/// is not present (build it with <c>docker build --platform linux/amd64 -t python-runner:1 runner</c>).
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public const string RunnerImage = "python-runner:1";

    private static readonly Lazy<string?> SkipReason = new(Check);

    public DockerFactAttribute()
    {
        if (SkipReason.Value is { } reason)
        {
            Skip = reason;
        }
    }

    public static IDockerClient CreateClient() =>
        new DockerClientConfiguration(new Uri(new FederatedTaskSettings().DockerEndpoint)).CreateClient();

    private static string? Check()
    {
        try
        {
            using var client = CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            client.Images.InspectImageAsync(RunnerImage, cts.Token).GetAwaiter().GetResult();
            return null;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return $"The image {RunnerImage} is not present: docker build --platform linux/amd64 -t {RunnerImage} runner";
        }
        catch (Exception ex)
        {
            return $"Docker is not reachable: {ex.GetType().Name}";
        }
    }
}
