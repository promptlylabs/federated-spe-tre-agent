using System.Net;
using System.Text;
using Agent.Api.FederatedTasks;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class EgressReviewClientTests
{
    private const string TaskId = "11111111-1111-1111-1111-111111111111";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? Request;
        public string? Body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content != null)
                Body = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FixedMonitor : IOptionsMonitor<FederatedEgressSettings>
    {
        public FixedMonitor(FederatedEgressSettings value) => CurrentValue = value;
        public FederatedEgressSettings CurrentValue { get; }
        public FederatedEgressSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<FederatedEgressSettings, string?> listener) => null;
    }

    /// <summary>Skips Keycloak: the token call is the only part not under test here.</summary>
    private sealed class TestClient : EgressReviewClient
    {
        public TestClient(IHttpClientFactory f, IOptionsMonitor<FederatedEgressSettings> k, IOptions<ApiEndpointSettings> e)
            : base(f, k, e, new ConfigurationBuilder().Build()) { }

        public int TokenCalls { get; private set; }

        protected override Task<string> GetTokenAsync()
        {
            TokenCalls++;
            return Task.FromResult("fake-token");
        }
    }

    private static EgressReviewClient Client(StubHandler handler, string clientId = "agent", string clientSecret = "secret",
        string baseUrl = "http://keycloak/realms/Data-Egress") =>
        new TestClient(
            new Factory(handler),
            new FixedMonitor(new FederatedEgressSettings { BaseUrl = baseUrl, ClientId = clientId, ClientSecret = clientSecret }),
            Options.Create(new ApiEndpointSettings { EgressApiUrl = "http://egress" }));

    private static EgressRegistration Registration() =>
        new(TaskId, "Count by sex", "study-1", "print(1)", "{\"n\":1}", "abc123", "federated-outputs");

    [Fact]
    public async Task Client_treats_already_registered_as_success()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"Result\":false}");

        await Client(handler).RegisterAsync(Registration(), CancellationToken.None);

        Assert.NotNull(handler.Request);
    }

    [Fact]
    public async Task Client_sends_kind_federated_study_code_result_and_one_file()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"Result\":true}");

        await Client(handler).RegisterAsync(Registration(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://egress/api/DataEgress/AddNewDataEgress/", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("fake-token", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);

        var json = JObject.Parse(handler.Body!);
        Assert.Equal(TaskId, (string?)json["SubmissionId"]);
        Assert.Equal("Count by sex", (string?)json["Name"]);
        Assert.Equal("federated", (string?)json["Kind"]);
        Assert.Equal("study-1", (string?)json["Study"]!["Id"]);
        Assert.Equal("print(1)", (string?)json["Code"]);
        Assert.Equal("{\"n\":1}", (string?)json["ResultContent"]);
        Assert.Equal("abc123", (string?)json["ResultSha256"]);
        Assert.Equal("federated-outputs", (string?)json["OutputBucket"]);
        var file = Assert.Single((JArray)json["Files"]!);
        Assert.Equal($"{TaskId}.json", (string?)file["Name"]);
        Assert.Equal(0, (int?)file["Status"]);
    }

    [Fact]
    public async Task Client_body_binds_to_the_shared_EgressSubmission_the_Egress_API_takes()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"Result\":true}");

        await Client(handler).RegisterAsync(Registration(), CancellationToken.None);

        var bound = JsonConvert.DeserializeObject<EgressSubmission>(handler.Body!)!;
        Assert.True(bound.IsFederated);
        Assert.Equal(EgressStatus.NotCompleted, bound.Status);
        Assert.Equal("study-1", bound.Study!.Id);
        Assert.Equal("federated-outputs", bound.OutputBucket);
        Assert.False(bound.DecisionDelivered);
        Assert.Null(bound.DecisionReason);
        var file = Assert.Single(bound.Files);
        Assert.Equal(FileStatus.Undecided, file.Status);
    }

    [Fact]
    public async Task Client_throws_on_non_success()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).RegisterAsync(Registration(), CancellationToken.None));
    }

    [Theory]
    [InlineData("agent", "secret", "http://keycloak/realms/Data-Egress", true)]
    [InlineData("", "secret", "http://keycloak/realms/Data-Egress", false)]
    [InlineData("agent", "", "http://keycloak/realms/Data-Egress", false)]
    [InlineData("agent", "secret", "", false)]
    public void Client_is_configured_only_with_base_url_id_and_secret(string id, string secret, string baseUrl, bool expected)
    {
        Assert.Equal(expected, Client(new StubHandler(HttpStatusCode.OK, ""), id, secret, baseUrl).IsConfigured);
    }

    [Fact]
    public async Task Client_withdraw_posts_the_task_id_and_reason()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"Result\":true}");

        await Client(handler).WithdrawAsync(TaskId, "changed", CancellationToken.None);

        Assert.Equal("http://egress/api/DataEgress/WithdrawEgress/", handler.Request!.RequestUri!.ToString());
        Assert.Equal("fake-token", handler.Request.Headers.Authorization!.Parameter);
        var json = JObject.Parse(handler.Body!);
        Assert.Equal(TaskId, (string?)json["SubmissionId"]);
        Assert.Equal("changed", (string?)json["Reason"]);
    }

    [Fact]
    public async Task Client_fetches_one_token_for_several_registrations()
    {
        var client = (TestClient)Client(new StubHandler(HttpStatusCode.OK, "{\"Result\":true}"));

        await client.RegisterAsync(Registration(), CancellationToken.None);
        await client.RegisterAsync(Registration(), CancellationToken.None);
        await client.RegisterAsync(Registration(), CancellationToken.None);

        Assert.Equal(1, client.TokenCalls);
    }

    [Fact]
    public void Native_egress_settings_do_not_configure_the_federated_client()
    {
        // The shipped appsettings fill DataEgressKeyCloakSettings for native egress; that section is not read here.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataEgressKeyCloakSettings:BaseUrl"] = "https://auth2.ukserp.ac.uk/realms/Data-Egress",
            ["DataEgressKeyCloakSettings:ClientId"] = "Data-Egress-API",
            ["DataEgressKeyCloakSettings:ClientSecret"] = "shipped",
        }).Build();
        var settings = new FederatedEgressSettings();
        config.GetSection(FederatedEgressSettings.SectionName).Bind(settings);

        Assert.False(settings.IsComplete);
    }
}
