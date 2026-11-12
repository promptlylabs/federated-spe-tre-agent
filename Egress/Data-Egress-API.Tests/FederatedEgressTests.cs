using FiveSafesTes.Core.Models.FederatedTasks;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.ViewModels;
using FiveSafesTes.Core.Services;
using DARE_Egress.Services;
using Data_Egress_API.Controllers;
using Data_Egress_API.Repositories.DbContexts;
using Data_Egress_API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Data_Egress_API.Tests;

public class FederatedEgressTests
{
    private const string Sha = "3f2a9c";
    private const string ResultJson = "{\"rows\":[{\"n\":12}]}";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly FakeTreClient _tre = new();

    private ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(_dbName)
            .UseLazyLoadingProxies()
            .Options);

    private DataEgressController NewController(ApplicationDbContext db, string reviewer = "reviewer")
    {
        var controller = new DataEgressController(db, _tre, ThrowingProxy<IMinioHelper>.Create(),
            ThrowingProxy<IDareEmailService>.Create(), new EmailSettings(), ThrowingProxy<IKeyCloakService>.Create());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("preferred_username", reviewer) }, "test"))
            }
        };
        return controller;
    }

    private (int egressId, int fileId) Seed(string? kind, bool extraFile = false)
    {
        using var db = NewDb();
        var row = new EgressSubmission
        {
            SubmissionId = "task-1",
            Status = EgressStatus.NotCompleted,
            OutputBucket = "results",
            Name = "Federated task 1",
            Files = new List<EgressFile> { new() { Name = "task-1.json", Status = FileStatus.Undecided } }
        };
        if (kind != null)
        {
            row.Kind = kind;
            row.Study = new FederatedStudy { Id = "study-1" };
            row.Code = "print('hello')";
            row.ResultContent = ResultJson;
            row.ResultSha256 = Sha;
        }
        if (extraFile)
        {
            row.Files.Add(new EgressFile { Name = "task-1-extra.json", Status = FileStatus.Undecided });
        }
        db.EgressSubmissions.Add(row);
        db.SaveChanges();
        return (row.Id, row.Files[0].Id);
    }

    private static EgressSubmission Form(int egressId, int fileId, FileStatus status, string? reason) => new()
    {
        Id = egressId,
        DecisionReason = reason,
        Files = new List<EgressFile> { new() { Id = fileId, Status = status } }
    };

    private EgressSubmission Load(int egressId) => NewDb().EgressSubmissions.First(x => x.Id == egressId);

    [Fact]
    public async Task CompleteEgress_federated_rejection_without_reason_is_refused_and_nothing_is_sent()
    {
        var (id, fileId) = Seed("federated");

        var result = await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "  "));

        AssertRefused(result, "A reason is required to reject a federated result");
        Assert.Empty(_tre.Sent);
        var row = Load(id);
        Assert.Equal(EgressStatus.NotCompleted, row.Status);
        Assert.Equal(FileStatus.Undecided, row.Files[0].Status);
    }

    [Fact]
    public async Task CompleteEgress_federated_undecided_file_is_refused_and_nothing_changes()
    {
        var (id, fileId) = Seed("federated");

        var result = await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Undecided, null));

        AssertRefused(result, "Not all files reviewed");
        Assert.Empty(_tre.Sent);
        Assert.Equal(EgressStatus.NotCompleted, Load(id).Status);
    }

    private static void AssertRefused(ActionResult<EgressSubmission> result, string message)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(message, bad.Value);
    }

    [Fact]
    public async Task CompleteEgress_federated_saves_the_decision_before_calling_back()
    {
        var (id, fileId) = Seed("federated");
        _tre.Throw = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells")));

        var row = Load(id);
        Assert.Equal(EgressStatus.FullyRejected, row.Status);
        Assert.Equal("small cells", row.DecisionReason);
        Assert.False(row.DecisionDelivered);
    }

    [Fact]
    public async Task CompleteEgress_federated_sends_reviewer_time_reason_and_sha()
    {
        var (id, fileId) = Seed("federated");

        await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells"));

        var sent = Assert.Single(_tre.Sent);
        Assert.Equal("small cells", sent.Reason);
        Assert.Equal("reviewer", sent.Reviewer);
        Assert.Equal(Sha, sent.ResultSha256);
        Assert.NotNull(sent.DecidedAt);
        var file = Assert.Single(sent.FileResults);
        Assert.False(file.Approved);
        Assert.True(Load(id).DecisionDelivered);
    }

    [Fact]
    public async Task CompleteEgress_federated_retry_resends_the_stored_decision_not_the_new_form()
    {
        var (id, fileId) = Seed("federated");
        _tre.Throw = true;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells")));
        var decidedAt = Load(id).Completed;

        _tre.Throw = false;
        await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Approved, null));

        var sent = Assert.Single(_tre.Sent);
        Assert.False(Assert.Single(sent.FileResults).Approved);
        Assert.Equal("small cells", sent.Reason);
        Assert.Equal(decidedAt, sent.DecidedAt);
        var row = Load(id);
        Assert.True(row.DecisionDelivered);
        Assert.Equal(EgressStatus.FullyRejected, row.Status);
    }

    [Fact]
    public async Task CompleteEgress_federated_other_reviewer_cannot_retry_a_stored_decision()
    {
        var (id, fileId) = Seed("federated");
        _tre.Throw = true;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells")));

        _tre.Throw = false;
        var result = await NewController(NewDb(), "second-reviewer")
            .CompleteEgressAsync(Form(id, fileId, FileStatus.Approved, null));

        AssertRefused(result, "This request was just decided by someone else. Reload it to see the decision.");
        Assert.Empty(_tre.Sent);
        var row = Load(id);
        Assert.False(row.DecisionDelivered);
        Assert.Equal("reviewer", row.Reviewer);
    }

    private static EgressSubmission Registration(string content, string sha) => new()
    {
        SubmissionId = "task-2",
        Kind = "federated",
        Name = "Federated task 2",
        OutputBucket = "results",
        ResultContent = content,
        ResultSha256 = sha,
        Files = new List<EgressFile> { new() { Name = "task-2.json" } }
    };

    [Fact]
    public async Task AddNewDataEgress_federated_result_matching_its_sha_is_stored()
    {
        var sha = FederatedResultDocumentJson.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(ResultJson));

        var result = await NewController(NewDb()).AddNewDataEgress(Registration(ResultJson, sha));

        Assert.True(result.Value!.Result);
        Assert.Equal(sha, NewDb().EgressSubmissions.Single(x => x.SubmissionId == "task-2").ResultSha256);
    }

    [Fact]
    public async Task AddNewDataEgress_federated_result_not_matching_its_sha_is_refused_and_not_stored()
    {
        var result = await NewController(NewDb()).AddNewDataEgress(Registration(ResultJson, Sha));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("The result does not match its SHA-256", bad.Value);
        Assert.False(NewDb().EgressSubmissions.Any(x => x.SubmissionId == "task-2"));
    }

    [Fact]
    public async Task CompleteEgress_native_is_unchanged()
    {
        var (id, fileId) = Seed(null);

        await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Approved, null));

        var sent = Assert.Single(_tre.Sent);
        Assert.Null(sent.Reason);
        Assert.Null(sent.ResultSha256);
        Assert.Null(sent.Reviewer);
        Assert.Null(sent.DecidedAt);
        Assert.True(Assert.Single(sent.FileResults).Approved);
        Assert.Equal(EgressStatus.FullyApproved, Load(id).Status);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Approved, null)));
        Assert.Equal("Egress has already been completed", ex.Message);
        Assert.Single(_tre.Sent);
    }

    [Fact]
    public async Task CompleteEgress_federated_already_delivered_throws()
    {
        var (id, fileId) = Seed("federated");
        await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells")));

        Assert.Equal("Egress has already been completed", ex.Message);
        Assert.Single(_tre.Sent);
    }

    [Fact]
    public async Task UpdateFileData_federated_completed_but_undelivered_is_refused_and_retry_sends_the_rejection()
    {
        var (id, fileId) = Seed("federated");
        _tre.Throw = true;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Rejected, "small cells")));
        _tre.Throw = false;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewController(NewDb()).UpdateFileData(fileId, FileStatus.Approved));
        Assert.Equal(FileStatus.Rejected, Load(id).Files[0].Status);

        await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Approved, null));
        var sent = Assert.Single(_tre.Sent);
        Assert.False(Assert.Single(sent.FileResults).Approved);
        Assert.Equal("small cells", sent.Reason);
    }

    [Fact]
    public async Task CompleteEgress_federated_empty_file_list_is_refused_and_nothing_changes()
    {
        var (id, _) = Seed("federated");
        var form = new EgressSubmission { Id = id, Files = new List<EgressFile>() };

        var result = await NewController(NewDb()).CompleteEgressAsync(form);

        AssertRefused(result, "The review must decide every file of the federated result");

        Assert.Empty(_tre.Sent);
        var row = Load(id);
        Assert.Equal(EgressStatus.NotCompleted, row.Status);
        Assert.Null(row.Completed);
    }

    [Fact]
    public async Task CompleteEgress_federated_form_missing_a_file_is_refused_and_nothing_changes()
    {
        var (id, fileId) = Seed("federated", extraFile: true);
        // The second file was already rejected outside the form (e.g. via UpdateFileData).
        await NewController(NewDb()).UpdateFileData(fileId + 1, FileStatus.Rejected);

        var result = await NewController(NewDb()).CompleteEgressAsync(Form(id, fileId, FileStatus.Approved, null));

        AssertRefused(result, "The review must decide every file of the federated result");
        Assert.Empty(_tre.Sent);
        var row = Load(id);
        Assert.Equal(EgressStatus.NotCompleted, row.Status);
        Assert.Equal(FileStatus.Undecided, row.Files.Single(f => f.Id == fileId).Status);
    }

    [Fact]
    public async Task DownloadFile_federated_returns_the_stored_result_content()
    {
        var (_, fileId) = Seed("federated");

        var result = await NewController(NewDb()).DownloadFileAsync(fileId);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(Encoding.UTF8.GetBytes(ResultJson), file.FileContents);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal("task-1.json", file.FileDownloadName);
    }

    private sealed class FakeTreClient : ITreClientWithoutTokenHelper
    {
        public List<EgressReview> Sent { get; } = new();
        public bool Throw { get; set; }

        public Task<TOutput?> CallAPI<TInput, TOutput>(string endPoint, TInput model,
            Dictionary<string, string>? paramList = null, bool usePut = false)
            where TInput : class? where TOutput : class?, new()
        {
            if (Throw) throw new HttpRequestException("TRE Agent unreachable");
            if (model is EgressReview review) Sent.Add(review);
            return Task.FromResult<TOutput?>(new TOutput());
        }

        public bool CheckCredsAreAvailable() => true;
        public Task<TOutput?> CallAPIToSendFile<TOutput>(string endPoint, string fileParamaterName, IFormFile file,
            Dictionary<string, string>? paramList = null) where TOutput : class?, new() => throw new NotSupportedException();
        public Task<string> GetTokenForUser(string username, string password, string requiredRole) =>
            throw new NotSupportedException();
        public Task<HttpResponseMessage> CallAPI(string endPoint, StringContent jsonString,
            Dictionary<string, string>? paramList = null, bool usePut = false) => throw new NotSupportedException();
        public Task<TOutput?> CallAPIWithoutModel<TOutput>(string endPoint, Dictionary<string, string>? paramList = null,
            HttpMethod httpMethod = null!) where TOutput : class?, new() => throw new NotSupportedException();
        public Task<byte[]> CallAPIToGetFile(string endPoint, Dictionary<string, string>? paramList = null) =>
            throw new NotSupportedException();
        public Task<TOutput?> CallAPIDelete<TOutput>(string endPoint, Dictionary<string, string>? paramList = null)
            where TOutput : class?, new() => throw new NotSupportedException();
    }

    /// <summary>Any call on the proxied interface throws, so a test fails if the controller touches it.</summary>
    public class ThrowingProxy<T> : DispatchProxy where T : class
    {
        public static T Create() => Create<T, ThrowingProxy<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} must not be called");
    }
}
