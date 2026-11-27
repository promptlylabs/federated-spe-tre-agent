using System.Reflection;
using System.Security.Claims;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Submission.Api.Controllers;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests;

/// <summary>The standard GA4GH TES routes, which serve federated studies only (ANZE-6134).</summary>
public class TesRoutesTests
{
    private const string Code = "def site_analysis(df):\n    return {'n': len(df)}\n\n\ndef aggregate(results):\n    return {'n': sum(r['n'] for r in results)}\n";

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var (id, name) in new[] { (1, "tre-a"), (2, "tre-b") })
        {
            db.Tres.Add(new Tre { Id = id, Name = name, AdminUsername = name + "-admin", KeycloakClientId = name + "-client", About = "", FormData = "" });
        }

        db.SaveChanges();
        return db;
    }

    private static TaskServiceApiController Tes(ApplicationDbContext db) =>
        new(db, new FederatedTaskSettings(), new AgentPollDispatch())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "daams")], "test")) }
            }
        };

    /// <summary>The TES controller as a hospital's Agent calls it: its Keycloak client, as GetAgentTre reads it.</summary>
    private static TaskServiceApiController Hospital(ApplicationDbContext db, string tre) =>
        new(db, new FederatedTaskSettings(), new AgentPollDispatch())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("azp", tre + "-client"), new Claim("preferred_username", "service-account-" + tre + "-client")], "test"))
                }
            }
        };

    private static TesTask Study(string studyId = "study-1", string name = "cohort study")
    {
        var task = FederatedTesProfile.BuildCodeTask("cohort_2024", Code, studyId, "tre-a|tre-b");
        task.Name = name;
        return task;
    }

    private static async Task<Guid> Submit(ApplicationDbContext db, string studyId = "study-1", string name = "cohort study") =>
        Guid.Parse(Assert.IsType<TesCreateTaskResponse>(Assert.IsType<OkObjectResult>(await Tes(db).CreateTaskAsync(Study(studyId, name), default)).Value).Id);

    private static void AssertError(IActionResult result, int status, string code)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        Assert.Equal(code, Assert.IsType<FederatedApiError>(obj.Value).ErrorCode);
    }

    /// <summary>The JSON a TES client receives, with the view's serializer settings.</summary>
    private static JObject Json(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(200, json.StatusCode);
        return JObject.Parse(JsonConvert.SerializeObject(json.Value, (JsonSerializerSettings)json.SerializerSettings!));
    }

    [Fact]
    public void Routes_need_the_admin_role_or_for_reading_a_hospitals_login()
    {
        var reads = new[] { nameof(TaskServiceApiController.ListTasks), nameof(TaskServiceApiController.GetTaskAsync) };
        foreach (var action in typeof(TaskServiceApiController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var roles = action.GetCustomAttribute<AuthorizeAttribute>()?.Roles;
            if (action.Name == nameof(TaskServiceApiController.GetServiceInfo))
            {
                Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
            }
            else
            {
                Assert.Equal(reads.Contains(action.Name) ? "dare-control-admin,dare-tre-admin" : "dare-control-admin", roles);
            }
        }
    }

    // A hospital fetches its work with the standard ListTasks: its own unfinished parts, never another hospital's, never a
    // study version.
    [Fact]
    public async Task A_hospital_lists_only_its_own_pending_parts()
    {
        using var db = NewDb();
        await Submit(db, "study-1");
        await Submit(db, "study-2");
        var mine = db.FederatedTasks.Where(t => t.TreName == "tre-a").Select(t => t.TaskId.ToString()).ToList();

        var list = Json(await Hospital(db, "tre-a").ListTasks(null, null, null, "FULL", default));

        Assert.Equal(mine.Order(), list["tasks"]!.Select(t => (string)t["id"]!).Order());
        Assert.All(list["tasks"]!, t => Assert.Equal("tre-a", (string?)t["tags"]!["fed.sites"]));
        Assert.Null((string?)list["next_page_token"]);
    }

    [Theory]
    [InlineData("own part", 200)]
    [InlineData("other hospital's part", 404)]
    [InlineData("version", 404)]
    public async Task A_hospital_reads_only_its_own_parts(string what, int status)
    {
        using var db = NewDb();
        var version = await Submit(db);
        var id = what switch
        {
            "own part" => db.FederatedTasks.Single(t => t.TreName == "tre-a").TaskId.ToString(),
            "other hospital's part" => db.FederatedTasks.Single(t => t.TreName == "tre-b").TaskId.ToString(),
            _ => version.ToString()
        };

        var result = await Hospital(db, "tre-a").GetTaskAsync(id, "BASIC", default);

        if (status == 404) AssertError(result, 404, FederatedTaskErrorCodes.TaskNotFound);
        else Assert.Equal(id, (string?)Json(result)["id"]);
    }

    // TES 1.1 service-info is a GA4GH service-info: id, name, type (org.ga4gh / tes / 1.1.0), organization and version.
    [Fact]
    public void Service_info_describes_the_federated_profile()
    {
        var info = Assert.IsType<TesServiceInfo>(Assert.IsType<ObjectResult>(Tes(NewDb()).GetServiceInfo()).Value);

        Assert.Equal("SPMS federated TES", info.Name);
        Assert.Equal(("org.ga4gh", "tes", "1.1.0"), (info.Type.Group, info.Type.Artifact, info.Type.Version));
        Assert.False(string.IsNullOrEmpty(info.Id));
        Assert.False(string.IsNullOrEmpty(info.Organization.Name));
        Assert.False(string.IsNullOrEmpty(info.Organization.Url));
        Assert.False(string.IsNullOrEmpty(info.Version));
        foreach (var tag in new[] { "fed.sites", "fed.study_id", "fed.code_sha256", "fed.request_id" })
        {
            Assert.Contains(tag, info.Description);
        }
    }

    [Fact]
    public async Task List_filters_by_state_and_tags()
    {
        using var db = NewDb();
        var open = await Submit(db, "study-1");
        var cancelled = await Submit(db, "study-2");
        await Tes(db).CancelTask(cancelled.ToString(), default);

        async Task<string[]> Ids(string? state, string[]? keys, string[]? values) =>
            Json(await Tes(db).ListTasks(null, null, null, "MINIMAL", default, state, keys, values))["tasks"]!.Select(t => (string)t["id"]!).ToArray();

        Assert.Equal([open.ToString()], await Ids("RUNNING", null, null));
        Assert.Equal([cancelled.ToString()], await Ids("CANCELED", null, null));
        Assert.Equal([cancelled.ToString()], await Ids(null, ["fed.study_id"], ["study-2"]));
        Assert.Equal(2, (await Ids(null, ["fed.study_id"], null)).Length);          // key only: any value
        Assert.Empty(await Ids(null, ["fed.study_id", "fed.sites"], ["study-1", "tre-a"]));   // every pair must match
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(await Tes(db).ListTasks(null, null, null, "MINIMAL", default, "BOGUS")).StatusCode);
    }

    [Fact]
    public async Task Create_refuses_a_task_without_fed_sites()
    {
        using var db = NewDb();
        var task = Study();
        task.Tags!.Remove("fed.sites");

        AssertError(await Tes(db).CreateTaskAsync(task, default), 400, FederatedTaskErrorCodes.InvalidTask);
        Assert.Empty(db.FederatedStudyVersions);
    }

    [Fact]
    public async Task Create_applies_the_shared_intake()
    {
        using var db = NewDb();
        var oneSite = Study();
        oneSite.Tags!["fed.sites"] = "tre-a";

        AssertError(await Tes(db).CreateTaskAsync(oneSite, default), 400, FederatedTaskErrorCodes.InvalidTask);
        var id = await Submit(db);

        Assert.Equal(id, db.FederatedStudyVersions.Single().VersionId);
        Assert.Single(db.AuditLogs.Where(a => a.LogType == LogType.FederatedStudySubmitted));
    }

    [Theory]
    [InlineData("version", 200)]
    [InlineData("part", 200)]
    [InlineData("unknown", 404)]
    [InlineData("42", 404)]
    public async Task Get_by_id(string what, int status)
    {
        using var db = NewDb();
        var version = await Submit(db);
        var part = db.FederatedTasks.First(t => t.TreName == "tre-a").TaskId;
        var id = what switch { "version" => version.ToString(), "part" => part.ToString(), "unknown" => Guid.NewGuid().ToString(), _ => what };

        var result = await Tes(db).GetTaskAsync(id, "FULL", default);

        if (status == 404)
        {
            AssertError(result, 404, FederatedTaskErrorCodes.TaskNotFound);
            return;
        }

        var task = Json(result);
        Assert.Equal(id, (string?)task["id"]);
        Assert.Equal(what == "version" ? "study-1" : null, (string?)task["logs"]?[0]?["metadata"]?["study_id"]);
        Assert.Equal(what == "part" ? "queued" : null, (string?)task["logs"]?[0]?["metadata"]?["phase"]);
    }

    // DAAMS is close to the researcher: a released hospital part shows its phase, never its result's hash or location (a small
    // JSON result could be guessed from its hash).
    [Fact]
    public async Task A_released_part_shows_no_result_details()
    {
        using var db = NewDb();
        await Submit(db);
        var part = db.FederatedTasks.First();
        part.MoveTo(FederatedTaskPhase.Running, DateTime.UtcNow);
        part.MoveTo(FederatedTaskPhase.WaitingForOutputApproval, DateTime.UtcNow);
        part.MoveTo(FederatedTaskPhase.Released, DateTime.UtcNow);
        (part.ResultBucket, part.ResultKey, part.ResultSha256) = ("federated-results", "1/x/abc.json", new string('a', 64));
        db.SaveChanges();

        var metadata = Json(await Tes(db).GetTaskAsync(part.TaskId.ToString(), "FULL", default))["logs"]![0]!["metadata"]!;

        Assert.Equal("released", (string?)metadata["phase"]);
        Assert.Null(metadata["result_sha256"]);
        Assert.Null(metadata["result_key"]);
        Assert.Null(metadata["result_bucket"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("MINIMAL")]
    [InlineData("BASIC")]
    [InlineData("FULL")]
    [InlineData("bogus")]
    public async Task Views(string? view)
    {
        using var db = NewDb();
        var id = (await Submit(db)).ToString();

        var result = await Tes(db).GetTaskAsync(id, view!, default);

        if (view == "bogus")
        {
            Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
            return;
        }

        var task = Json(result);
        if (view is null or "MINIMAL")
        {
            Assert.Equal(["id", "state"], task.Properties().Select(p => p.Name).Order());
            return;
        }

        var code = (string?)task["inputs"]![0]!["content"];
        Assert.Equal(view == "FULL" ? Code : null, code);
    }

    [Fact]
    public async Task List_newest_first_with_paging()
    {
        using var db = NewDb();
        var oldest = await Submit(db, "study-1", "alpha one");
        var middle = await Submit(db, "study-2", "alpha two");
        var newest = await Submit(db, "study-3", "beta");
        // The in-memory clock can tie: make the order explicit.
        var at = DateTime.UtcNow;
        foreach (var (id, minutes) in new[] { (oldest, -3), (middle, -2), (newest, -1) })
        {
            db.FederatedStudyVersions.Single(v => v.VersionId == id).SubmittedAt = at.AddMinutes(minutes);
        }

        db.SaveChanges();

        var first = Json(await Tes(db).ListTasks(null!, 2, null!, "MINIMAL", default));
        Assert.Equal([newest.ToString(), middle.ToString()], first["tasks"]!.Select(t => (string)t["id"]!));
        var token = (string)first["next_page_token"]!;
        var second = Json(await Tes(db).ListTasks(null!, 2, token, "MINIMAL", default));
        Assert.Equal([oldest.ToString()], second["tasks"]!.Select(t => (string)t["id"]!));
        Assert.Null((string?)second["next_page_token"]);

        var alpha = Json(await Tes(db).ListTasks("alpha", null, null!, "MINIMAL", default));
        Assert.Equal([middle.ToString(), oldest.ToString()], alpha["tasks"]!.Select(t => (string)t["id"]!));
    }

    [Theory]
    [InlineData("open", 200)]
    [InlineData("finished", 409)]
    [InlineData("part", 400)]
    [InlineData("unknown", 404)]
    public async Task Cancel(string what, int status)
    {
        using var db = NewDb();
        var version = await Submit(db);
        var part = db.FederatedTasks.First().TaskId;
        if (what == "finished")
        {
            var v = db.FederatedStudyVersions.Single();
            v.Outcome = FederatedStudyOutcome.Aggregated;
            db.SaveChanges();
        }

        var id = what switch { "part" => part.ToString(), "unknown" => Guid.NewGuid().ToString(), _ => version.ToString() };
        var result = await Tes(db).CancelTask(id, default);

        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        var stored = db.FederatedStudyVersions.Include(v => v.Parts).Single();
        switch (what)
        {
            case "open":
                Assert.IsType<TesCancelTaskResponse>(((ObjectResult)result).Value);
                Assert.Equal(FederatedStudyOutcome.Canceled, stored.Outcome);
                Assert.All(stored.Parts, p => Assert.Equal(FederatedTaskPhase.Canceled, p.Phase));
                break;
            case "finished":
                AssertError(result, 409, FederatedTaskErrorCodes.TaskAlreadyFinished);
                break;
            case "part":
                AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
                Assert.Contains(version.ToString(), ((FederatedApiError)((ObjectResult)result).Value!).ErrorMessage);
                Assert.Null(stored.Outcome);
                break;
            default:
                AssertError(result, 404, FederatedTaskErrorCodes.TaskNotFound);
                break;
        }
    }
}
