using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Federated.IntegrationTests;

/// <summary>The poll trigger is best effort: a dashboard that does not answer must not fail a test.</summary>
public class AgentPollTests
{
    [Fact]
    public async Task Trigger_returns_false_when_the_dashboard_accepts_but_never_answers()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var poll = new AgentPoll(new Uri($"http://127.0.0.1:{port}"), TimeSpan.FromSeconds(1));

            Assert.False(await poll.TriggerAsync());
        }
        finally
        {
            listener.Stop();
        }
    }
}
