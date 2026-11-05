using System.Net;
using System.Text;
using Agent.Api.FederatedTasks;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class FederatedTaskClientTests
{
    private sealed class FakeTokens : IFederatedTokenSource
    {
        public Task<string> GetTokenAsync() => Task.FromResult("fake-token");
    }

    private sealed class CapturedPart
    {
        public string Name = "";
        public string? FileName;
        public string? ContentType;
        public byte[] Bytes = Array.Empty<byte>();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? Request;
        public List<CapturedPart> Parts { get; } = new();

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    Parts.Add(new CapturedPart
                    {
                        Name = part.Headers.ContentDisposition!.Name!.Trim('"'),
                        FileName = part.Headers.ContentDisposition.FileName?.Trim('"'),
                        ContentType = part.Headers.ContentType?.MediaType,
                        Bytes = await part.ReadAsByteArrayAsync(ct)
                    });
                }
            }
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static FederatedTaskClient Client(StubHandler handler, ConfigInputMethod method = ConfigInputMethod.Upload,
        string clientId = "tre-client", string clientSecret = "secret", IFederatedTokenSource? tokens = null)
    {
        var settings = new SubmissionKeyCloakSettings { ConfigInputMethod = method, ClientId = clientId, ClientSecret = clientSecret };
        return new FederatedTaskClient(
            new Factory(handler),
            new FixedMonitor(settings),
            Options.Create(new ApiEndpointSettings { SubmissionApiUrl = "http://submission" }),
            tokens ?? new FakeTokens());
    }

    private sealed class FixedMonitor : IOptionsMonitor<SubmissionKeyCloakSettings>
    {
        public FixedMonitor(SubmissionKeyCloakSettings value) => CurrentValue = value;
        public SubmissionKeyCloakSettings CurrentValue { get; }
        public SubmissionKeyCloakSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SubmissionKeyCloakSettings, string?> listener) => null;
    }

    private static FederatedTaskResultReport Complete() => new()
    {
        TaskId = "11111111-1111-1111-1111-111111111111",
        State = TesState.COMPLETEEnum,
        DatasetSha256 = "abc"
    };

    private static FederatedTaskResultReport Failed() => new()
    {
        TaskId = "11111111-1111-1111-1111-111111111111",
        State = TesState.EXECUTORERROREnum,
        ErrorCode = "dataset_not_found",
        ErrorMessage = "no dataset",
        DatasetSha256 = "def"
    };

    private static string Field(StubHandler h, string name) =>
        Encoding.UTF8.GetString(Assert.Single(h.Parts, p => p.Name == name).Bytes);

    [Fact]
    public async Task Report_complete_sends_multipart_with_result_part_and_no_dataset_sha_field()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"result\":true}");
        var file = Encoding.UTF8.GetBytes("{\"schema_version\":1}");

        await Client(handler).ReportAsync(Complete(), file);

        Assert.Equal("multipart/form-data", handler.Request!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("fake-token", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal("http://submission/api/FederatedTask/ReportResult", handler.Request.RequestUri!.ToString());
        Assert.Equal("COMPLETE", Field(handler, "State"));
        Assert.Equal("11111111-1111-1111-1111-111111111111", Field(handler, "TaskId"));
        var part = Assert.Single(handler.Parts, p => p.Name == "result");
        Assert.Equal(file, part.Bytes);
        Assert.Equal("result.json", part.FileName);
        Assert.Equal("application/json", part.ContentType);
        Assert.DoesNotContain(handler.Parts, p => p.Name == "DatasetSha256");
    }

    [Fact]
    public async Task Report_error_sends_fields_and_no_file()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"result\":true}");

        await Client(handler).ReportAsync(Failed(), null);

        Assert.Equal("EXECUTOR_ERROR", Field(handler, "State"));
        Assert.Equal("dataset_not_found", Field(handler, "ErrorCode"));
        Assert.Equal("no dataset", Field(handler, "ErrorMessage"));
        Assert.Equal("def", Field(handler, "DatasetSha256"));
        Assert.DoesNotContain(handler.Parts, p => p.Name == "result");
    }

    [Fact]
    public async Task Report_parses_200_location()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"result\":true,\"result_bucket\":\"b\",\"result_key\":\"1/t/h.json\",\"result_sha256\":\"h\"}");

        var outcome = await Client(handler).ReportAsync(Complete(), new byte[] { 1 });

        Assert.True(outcome.Accepted);
        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("1/t/h.json", outcome.ResultKey);
        Assert.Equal("h", outcome.ResultSha256);
    }

    [Fact]
    public async Task Report_parses_400_error_code()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"error_code\":\"invalid_result\",\"error_message\":\"m\"}");

        var outcome = await Client(handler).ReportAsync(Complete(), new byte[] { 1 });

        Assert.False(outcome.Accepted);
        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal("invalid_result", outcome.ErrorCode);
        Assert.Equal("m", outcome.ErrorMessage);
    }

    [Fact]
    public async Task Report_parses_409_result_sha256()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict,
            "{\"error_code\":\"already_reported\",\"error_message\":\"m\",\"result_sha256\":\"stored\"}");

        var outcome = await Client(handler).ReportAsync(Complete(), new byte[] { 1 });

        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal("stored", outcome.ResultSha256);
    }

    [Fact]
    public async Task Report_non_json_error_body_gives_null_codes()
    {
        var handler = new StubHandler(HttpStatusCode.BadGateway, "<html>Bad gateway</html>");

        var outcome = await Client(handler).ReportAsync(Complete(), new byte[] { 1 });

        Assert.Equal(502, outcome.StatusCode);
        Assert.Null(outcome.ErrorCode);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public async Task GetPending_deserializes_tes_tasks_with_bearer_token()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[{\"id\":\"t1\",\"state\":\"QUEUED\"}]");

        var tasks = await Client(handler).GetPendingAsync();

        Assert.Equal("t1", Assert.Single(tasks).Id);
        Assert.Equal("fake-token", handler.Request!.Headers.Authorization!.Parameter);
        Assert.Equal("http://submission/api/FederatedTask/GetPendingTasksForTre", handler.Request.RequestUri!.ToString());
    }

    private sealed class ThrowingTokens : IFederatedTokenSource
    {
        public Task<string> GetTokenAsync() => throw new InvalidOperationException("keycloak down");
    }

    private sealed class EmptyTokens : IFederatedTokenSource
    {
        public Task<string> GetTokenAsync() => Task.FromResult("");
    }

    [Fact]
    public async Task Token_failure_propagates_from_report_and_get_pending()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var client = Client(handler, tokens: new ThrowingTokens());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReportAsync(Complete(), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetPendingAsync());
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Empty_token_is_never_sent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var client = Client(handler, tokens: new EmptyTokens());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReportAsync(Complete(), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetPendingAsync());
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(ConfigInputMethod.Upload, "id", "secret", true)]
    [InlineData(ConfigInputMethod.Manual, "id", "secret", false)]
    [InlineData(ConfigInputMethod.Upload, "", "secret", false)]
    [InlineData(ConfigInputMethod.Upload, "id", "", false)]
    public void IsConfigured_needs_upload_mode_and_client_credentials(ConfigInputMethod method, string id, string secret, bool expected)
    {
        var client = Client(new StubHandler(HttpStatusCode.OK, "{}"), method, id, secret);
        Assert.Equal(expected, client.IsConfigured);
    }
}
