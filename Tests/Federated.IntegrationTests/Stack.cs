using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Federated.IntegrationTests;

/// <summary>
/// Id is the my-tre part's task ID (null when my-tre is not among the hospitals). VersionId is the study version
/// Enqueue created, or on a 409 study_version_open the version still open.
/// </summary>
public record EnqueueOutcome(int Status, string? Id, string? ErrorCode, string? VersionId = null);
public record TaskView(string State, string? Phase, string? ErrorCode, string? ErrorMessage, string? ResultSha256, string? ScanRulesVersion = null);
public record ResultView(byte[] Bytes, string Sha256);
public record HospitalPart(string Tre, string TaskId, string Phase);

/// <summary>
/// A study version as GetStudyVersion returns it: open, canceling or ended, its outcome once decided, and each hospital's
/// phase.
/// </summary>
public record StudyView(int Version, string State, IReadOnlyList<HospitalPart> Hospitals, string? Outcome = null,
    string? OutcomeReason = null, string? OutcomeMessage = null)
{
    public string PhaseOf(string tre) => Hospitals.Single(h => h.Tre == tre).Phase;
}

/// <summary>Addresses and dev logins of the stack in dev/federated (see its README). The host can be overridden.</summary>
public sealed class StackSettings
{
    public string Host { get; } = Environment.GetEnvironmentVariable("FEDERATED_STACK_HOST") is { Length: > 0 } h ? h : "localhost";
    public Uri Submission => new($"http://{Host}:5034");
    public Uri Agent => new($"http://{Host}:5269");
    public Uri Egress => new($"http://{Host}:5279");
    public Uri Keycloak => new($"http://{Host}:8180");
    public string RustFs => $"http://{Host}:9000";
    public string ResultsStore => $"http://{Host}:9100";
    public const string Tre = "my-tre";

    /// <summary>A second registered TRE with no Agent: its parts stay queued until they are cancelled.</summary>
    public const string OtherTre = "other-tre";

    /// <summary>A study names at least 2 hospitals. other-tre has no Agent on this stack, so its part stays queued.</summary>
    public const string BothHospitals = $"{Tre}|{OtherTre}";
    public const string Image = "python-runner:1";
}

/// <summary>Fetches a password-grant token from the dev Keycloak, and reuses it until shortly before it expires.</summary>
internal sealed class TokenSource(HttpClient http, Uri keycloak, string realm, string client, string secret, string user, string password)
{
    private string? cached;
    private DateTime renewAt;

    public async Task<string> GetAsync()
    {
        if (cached != null && DateTime.UtcNow < renewAt) return cached;
        using var response = await http.PostAsync(new Uri(keycloak, $"/realms/{realm}/protocol/openid-connect/token"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = client, ["client_secret"] = secret,
                ["username"] = user, ["password"] = password
            }));
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var lifetime = json["expires_in"]?.GetValue<int>() ?? 60;
        renewAt = DateTime.UtcNow.AddSeconds(Math.Max(0, lifetime - 30));
        return cached = json["access_token"]!.GetValue<string>();
    }
}

/// <summary>The orchestrator side: queues tasks and reads their state and stored result.</summary>
public sealed class SubmissionClient
{
    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly TokenSource token;

    internal SubmissionClient(HttpClient http, StackSettings s)
    {
        this.http = http;
        baseUri = s.Submission;
        token = new TokenSource(http, s.Keycloak, "Dare-Control", "Dare-Control-API", "dev-dare-control-api-secret", "control-admin", "ControlAdmin1!");
    }

    /// <summary>
    /// Queues a code task that runs <paramref name="code"/> on the dataset, in the approved runner image, for my-tre and
    /// a new study by default.
    /// </summary>
    public Task<EnqueueOutcome> EnqueueCodeAsync(string dataset, string code, string? image = null, string? sites = null, string? studyId = null) =>
        EnqueueBodyAsync(CodeTask(dataset, code, image ?? StackSettings.Image, sites, studyId));

    /// <summary>A new study ID by default, so no test waits for another test's open version.</summary>
    public static JsonObject FederationTags(string? sites = null, string? studyId = null) =>
        new() { ["fed.sites"] = sites ?? StackSettings.BothHospitals, ["fed.study_id"] = studyId ?? $"study-{Guid.NewGuid():N}" };

    /// <summary>The lowercase hex SHA-256 of the code's UTF-8 bytes, as the tag fed.code_sha256 must hold.</summary>
    public static string CodeSha256(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>
    /// A code task in the shape FederatedTesProfile.BuildCodeTask builds (see docs/federated-tasks.md, "Code tasks"):
    /// the exact run-analysis command, the code inline, the dataset, the result:// output and the fed.* tags.
    /// </summary>
    public static JsonObject CodeTask(string dataset, string code, string image = StackSettings.Image, string? sites = null, string? studyId = null)
    {
        var tags = FederationTags(sites, studyId);
        tags["fed.code_sha256"] = CodeSha256(code);
        return new JsonObject
        {
            ["executors"] = new JsonArray(new JsonObject
            {
                ["image"] = image,
                ["command"] = new JsonArray("run-analysis", "/code/analysis.py", $"/data/{dataset}", "/outputs/result.json")
            }),
            ["inputs"] = new JsonArray(
                new JsonObject { ["path"] = "/code/analysis.py", ["type"] = "FILE", ["content"] = code },
                new JsonObject { ["url"] = $"dataset://{dataset}", ["path"] = $"/data/{dataset}", ["type"] = "DIRECTORY" }),
            ["outputs"] = new JsonArray(new JsonObject { ["path"] = "/outputs/result.json", ["url"] = "result://", ["type"] = "FILE" }),
            ["tags"] = tags
        };
    }

    /// <summary>Posts any task body as it is, for the tasks Submission must refuse. On a 200, reads my-tre's part from the version.</summary>
    public async Task<EnqueueOutcome> EnqueueBodyAsync(JsonObject body)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/FederatedTask/Enqueue",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var json = TryParse(await response.Content.ReadAsStringAsync());
        if (!response.IsSuccessStatusCode)
            return new EnqueueOutcome((int)response.StatusCode, null, json?["error_code"]?.GetValue<string>(), json?["version_id"]?.GetValue<string>());
        var versionId = json!["id"]!.GetValue<string>();
        var study = await GetStudyVersionAsync(versionId);
        var mine = study.Hospitals.SingleOrDefault(h => h.Tre == StackSettings.Tre);
        return new EnqueueOutcome((int)response.StatusCode, mine?.TaskId, null, versionId);
    }

    public async Task<StudyView> GetStudyVersionAsync(string versionId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/FederatedTask/GetStudyVersion?id={versionId}");
        response.EnsureSuccessStatusCode();
        var view = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return new StudyView(view["version"]!.GetValue<int>(), view["state"]!.GetValue<string>(),
            view["hospitals"]!.AsArray()
                .Select(h => new HospitalPart(h!["tre"]!.GetValue<string>(), h["task_id"]!.GetValue<string>(), h["phase"]!.GetValue<string>()))
                .ToList(),
            view["outcome"]?.GetValue<string>(), view["outcome_reason"]?.GetValue<string>(), view["outcome_message"]?.GetValue<string>());
    }

    /// <summary>Null for an empty or non-JSON body (a 401, or a server error page), so the status code is still reported.</summary>
    internal static JsonNode? TryParse(string body)
    {
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    public async Task<TaskView> GetTaskAsync(string id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/FederatedTask/GetTask?id={id}");
        response.EnsureSuccessStatusCode();
        var task = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var meta = task["logs"]?.AsArray().FirstOrDefault()?["metadata"];
        string? Meta(string key) => meta?[key]?.GetValue<string>();
        return new TaskView(task["state"]!.GetValue<string>(), Meta("phase"), Meta("error_code"), Meta("error_message"), Meta("result_sha256"),
            Meta("scan_rules_version"));
    }

    /// <summary>Cancels a study version as an orchestrator admin. Returns the HTTP status and the version's state after it.</summary>
    public async Task<(int Status, string? State)> CancelAsync(string versionId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/FederatedTask/Cancel?id={versionId}");
        var json = TryParse(await response.Content.ReadAsStringAsync());
        return ((int)response.StatusCode, json?["state"]?.GetValue<string>());
    }

    /// <summary>The stored result, or null when Submission has none for the task.</summary>
    public async Task<ResultView?> GetResultAsync(string id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/FederatedTask/GetTaskResult?id={id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new ResultView(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync());
        return await http.SendAsync(request);
    }
}

/// <summary>The hospital reviewer in the Data Egress app (user reviewer).</summary>
public sealed class EgressClient
{
    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly TokenSource token;

    internal EgressClient(HttpClient http, StackSettings s)
    {
        this.http = http;
        baseUri = s.Egress;
        token = new TokenSource(http, s.Keycloak, "Data-Egress", "Data-Egress-API", "dev-data-egress-api-secret", "reviewer", "Reviewer1!");
    }

    /// <summary>The id of the task's undecided review request, once the Agent has registered it.</summary>
    public async Task<int> WaitForRowAsync(string taskId, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/DataEgress/GetAllEgresses?unprocessedonly=true");
            response.EnsureSuccessStatusCode();
            var row = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
                .FirstOrDefault(r => r?["submissionId"]?.GetValue<string>() == taskId);
            if (row != null) return row["id"]!.GetValue<int>();
            if (DateTime.UtcNow > until) throw new TimeoutException($"task {taskId}: no review request in the Egress app after {timeout}");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>True while the task's review request is still waiting for a reviewer in the Egress app.</summary>
    public async Task<bool> HasOpenRequestAsync(string taskId)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/DataEgress/GetAllEgresses?unprocessedonly=true");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Any(r => r?["submissionId"]?.GetValue<string>() == taskId);
    }

    /// <summary>Decides every file of the task's review request and completes it, as the Egress UI does.</summary>
    public async Task DecideAsync(string taskId, bool approve, string? reason)
    {
        var row = await WaitForRowAsync(taskId, TimeSpan.FromSeconds(60));
        using var get = await SendAsync(HttpMethod.Get, $"/api/DataEgress/GetEgress?id={row}");
        get.EnsureSuccessStatusCode();
        var egress = JsonNode.Parse(await get.Content.ReadAsStringAsync())!;
        foreach (var file in egress["files"]!.AsArray()) file!["status"] = approve ? 1 : 2;
        egress["decisionReason"] = reason;
        using var post = await SendAsync(HttpMethod.Post, "/api/DataEgress/CompleteEgress",
            new StringContent(egress.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!post.IsSuccessStatusCode)
            throw new InvalidOperationException($"CompleteEgress for task {taskId}: HTTP {(int)post.StatusCode} {await post.Content.ReadAsStringAsync()}");
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync());
        return await http.SendAsync(request);
    }
}

/// <summary>Runs the Agent's federated poll now (Hangfire "Trigger now"), instead of waiting up to a minute.</summary>
public sealed class AgentPoll
{
    private const string JobName = "Run Federated Tasks";
    private static readonly Regex CsrfToken = new("<meta name=\"csrf-token\" content=\"([^\"]+)\"", RegexOptions.Compiled);
    private readonly Uri agent;
    private readonly TimeSpan timeout;

    internal AgentPoll(StackSettings s) : this(s.Agent, TimeSpan.FromSeconds(15)) { }

    internal AgentPoll(Uri agent, TimeSpan timeout)
    {
        this.agent = agent;
        this.timeout = timeout;
    }

    /// <summary>Best effort: false when the dashboard did not accept the trigger (the minute poll still runs).</summary>
    public async Task<bool> TriggerAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var http = new HttpClient(handler) { BaseAddress = agent, Timeout = timeout };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:password")));
            var page = await http.GetStringAsync("/hangfire/recurring");
            var csrf = CsrfToken.Match(page);
            if (!csrf.Success) return false;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/hangfire/recurring/trigger")
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("jobs[]", JobName) })
            };
            request.Headers.Add("RequestVerificationToken", WebUtility.HtmlDecode(csrf.Groups[1].Value));
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Unreachable or too slow (HttpClient's timeout throws TaskCanceledException): the minute poll still runs.
            return false;
        }
    }
}

/// <summary>The TRE's RustFS: test datasets, and the saved results the Agent reviews.</summary>
public sealed class TreStorage : IDisposable
{
    private const string Datasets = "federated-datasets";
    private const string Outputs = "federated-outputs";
    private readonly AmazonS3Client s3;

    internal TreStorage(StackSettings s) =>
        s3 = new AmazonS3Client(new BasicAWSCredentials("rustfsadmin", "rustfsadmin123"),
            new AmazonS3Config { ServiceURL = s.RustFs, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    public Task ListDatasetsAsync() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Datasets, MaxKeys = 1 });

    public async Task<bool> HasDatasetAsync(string dataset)
    {
        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Datasets, Prefix = $"dataset/{dataset}/" });
        return (listed.S3Objects ?? []).Any(o => o.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
    }

    public Task PutDatasetFileAsync(string dataset, string fileName, string csv) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = Datasets, Key = $"dataset/{dataset}/{fileName}", ContentBody = csv });

    public async Task DeleteDatasetAsync(string dataset)
    {
        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Datasets, Prefix = $"dataset/{dataset}/" });
        foreach (var obj in listed.S3Objects ?? [])
            await s3.DeleteObjectAsync(Datasets, obj.Key);
    }

    /// <summary>True while the TRE holds the task's saved result, waiting for output approval.</summary>
    public async Task<bool> HasOutputAsync(string taskId)
    {
        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Outputs, Prefix = $"{taskId}.json" });
        return (listed.S3Objects ?? []).Count > 0;
    }

    public Task OverwriteResultAsync(string taskId, byte[] bytes) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = Outputs, Key = $"{taskId}.json", InputStream = new MemoryStream(bytes) });

    public void Dispose() => s3.Dispose();
}

/// <summary>
/// The orchestrator's result store, where Submission keeps released results ({tre}/{task}/{sha256}.json). Checked
/// directly: GetTaskResult answers 404 for every task that is not COMPLETE, whatever is stored.
/// </summary>
public sealed class ResultStore : IDisposable
{
    private const string Bucket = "federated-results";
    private readonly AmazonS3Client s3;

    internal ResultStore(StackSettings s) =>
        s3 = new AmazonS3Client(new BasicAWSCredentials("orchadmin", "orchadmin123"),
            new AmazonS3Config { ServiceURL = s.ResultsStore, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    public Task ListAsync() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 });

    /// <summary>True when any result file of the task is stored.</summary>
    public async Task<bool> HasResultAsync(string taskId)
    {
        var request = new ListObjectsV2Request { BucketName = Bucket };
        while (true)
        {
            var listed = await s3.ListObjectsV2Async(request);
            if ((listed.S3Objects ?? []).Any(o => o.Key.Contains($"/{taskId}/", StringComparison.OrdinalIgnoreCase))) return true;
            if (listed.IsTruncated != true) return false;
            request.ContinuationToken = listed.NextContinuationToken;
        }
    }

    public void Dispose() => s3.Dispose();
}

public static class Await
{
    /// <summary>
    /// How long a test waits for a task to leave a phase. A code task needs one Agent poll to start and a later one to
    /// collect, and the runner image may run under emulation (amd64 on Apple silicon), so this is well over a minute.
    /// </summary>
    public static readonly TimeSpan PhaseWait = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TriggerEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Polls GetTask until done(task) or the timeout. While the task waits for the Agent (phase queued, running or
    /// canceling), triggers the Agent's poll at the start and every 15 s.
    /// </summary>
    /// <summary>Polls GetStudyVersion until done(study) or the timeout. The sweep decides outcomes every 15 s on this stack.</summary>
    public static async Task<StudyView> UntilStudyAsync(StackFixture s, string versionId, Func<StudyView, bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var study = await s.Submission.GetStudyVersionAsync(versionId);
            if (done(study)) return study;
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"study version {versionId}: still {study.State}/{study.Outcome ?? "no outcome"} after {timeout}");
            await Task.Delay(PollEvery);
        }
    }

    public static async Task<TaskView> UntilAsync(StackFixture s, string id, Func<TaskView, bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        var nextTrigger = DateTime.UtcNow;
        TaskView view;
        while (true)
        {
            view = await s.Submission.GetTaskAsync(id);
            if (done(view)) return view;
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"task {id}: still {view.State}/{view.Phase} after {timeout}");
            if (view.Phase is "queued" or "running" or "canceling" && DateTime.UtcNow >= nextTrigger)
            {
                await s.Agent.TriggerAsync();
                nextTrigger = DateTime.UtcNow + TriggerEvery;
            }
            await Task.Delay(PollEvery);
        }
    }

    /// <summary>Waits until the Agent is done with the task for now: its phase is neither queued nor running.</summary>
    public static Task<TaskView> UntilSettledAsync(StackFixture s, string id, TimeSpan timeout) =>
        UntilAsync(s, id, t => t.Phase is not ("queued" or "running"), timeout);
}
