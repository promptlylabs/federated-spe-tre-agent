using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Submission.Api.Controllers;
using Submission.Api.Models;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests
{
    /// <summary>The pooled result read back and a study version's audit trail (ANZE-6133).</summary>
    public class FederatedStudyAuditTests
    {
        private static readonly DateTime At = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        private readonly ApplicationDbContext _db;
        private readonly FakeResultStore _store = new();

        public FederatedStudyAuditTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            _db.Tres.Add(new Tre { Id = 1, Name = "tre-a", AdminUsername = "a", KeycloakClientId = "tre-a-client", About = "", FormData = "" });
            _db.Tres.Add(new Tre { Id = 2, Name = "tre-b", AdminUsername = "b", KeycloakClientId = "tre-b-client", About = "", FormData = "" });
            _db.SaveChanges();
        }

        private static ControllerContext As(string user) => new()
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test")) }
        };

        private FederatedTaskController Studies(string user = "control-admin") =>
            new(_db, _store, new FederatedTaskSettings(), new AgentPollDispatch()) { ControllerContext = As(user) };

        private FederatedAggregationController Aggregation(string user) => new(_db, _store) { ControllerContext = As(user) };

        /// <summary>A version for tre-a and tre-b: tre-a released, tre-b released or rejected; aggregated when pooled is given.</summary>
        private FederatedStudyVersion Seed(bool bReleased, byte[]? pooled = null)
        {
            var version = new FederatedStudyVersion
            {
                VersionId = Guid.NewGuid(), StudyId = "study-1", Version = 1, Sites = "tre-a|tre-b", CodeSha256 = new string('c', 64),
                TesTaskJson = "{}", SubmittedAt = At, SubmittedBy = "control-admin", DeadlineAt = At.AddDays(7)
            };
            foreach (var (treId, tre, released) in new[] { (1, "tre-a", true), (2, "tre-b", bReleased) })
            {
                var part = new FederatedTask { TaskId = Guid.NewGuid(), TreId = treId, TreName = tre, StudyId = "study-1", TesTaskJson = "{}", SubmittedAt = At };
                part.MoveTo(FederatedTaskPhase.Queued, At);
                part.MoveTo(FederatedTaskPhase.Running, At.AddMinutes(1));
                if (released)
                {
                    part.MoveTo(FederatedTaskPhase.Released, At.AddMinutes(2));
                    part.ResultSha256 = new string(treId == 1 ? 'a' : 'b', 64);
                }
                else
                {
                    part.Reject(FederatedTaskPhase.RejectedAtOutputApproval, At.AddMinutes(2), FederatedTaskErrorCodes.RejectedByReviewer, "small cells");
                }

                version.Parts.Add(part);
            }

            if (!bReleased)
            {
                FederatedStudyOutcomes.Decide(version, At.AddMinutes(3));
            }
            else
            {
                FederatedStudyOutcomes.Decide(version, At.AddMinutes(3));
                version.AggregationStartedAt = At.AddMinutes(4);
                if (pooled != null)
                {
                    var sha = FederatedResultDocumentJson.Sha256Hex(pooled);
                    var key = FederatedResultKeys.ForPooled(version.VersionId, sha);
                    _store.Objects[key] = pooled;
                    FederatedStudyOutcomes.Aggregate(version, At.AddMinutes(5), _store.Bucket, key, sha);
                }
            }

            _db.FederatedStudyVersions.Add(version);
            _db.SaveChanges();
            return version;
        }

        private static void AssertError(IActionResult result, int status, string code)
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal((status, code), (obj.StatusCode!.Value, Assert.IsType<FederatedApiError>(obj.Value).ErrorCode));
        }

        [Fact]
        public async Task Enqueue_records_the_submitter_and_writes_an_audit_entry()
        {
            const string code = "def site_analysis(df):\n    return {'n': len(df)}\n\n\ndef aggregate(results):\n    return results[0]\n";
            var task = FederatedTesProfile.BuildCodeTask("cohort_2024", code, "study-9", "tre-a|tre-b");

            var result = await Studies("alice").Enqueue(task);

            var id = Guid.Parse(Assert.IsType<FiveSafesTes.Core.Models.Tes.TesCreateTaskResponse>(Assert.IsType<OkObjectResult>(result).Value).Id);
            Assert.Equal("alice", _db.FederatedStudyVersions.Single().SubmittedBy);
            var audit = Assert.Single(_db.AuditLogs);
            Assert.Equal((LogType.FederatedStudySubmitted, "alice"), (audit.LogType, audit.LoggedInUserName));
            Assert.Contains(id.ToString(), audit.HistoricFormData);
        }

        [Fact]
        public async Task Cancel_and_pooling_and_retry_write_audit_entries()
        {
            var open = new FederatedStudyVersion
            {
                VersionId = Guid.NewGuid(), StudyId = "study-1", Version = 1, Sites = "tre-a|tre-b", CodeSha256 = new string('c', 64),
                TesTaskJson = "{}", SubmittedAt = At, DeadlineAt = At.AddDays(7)
            };
            var queued = new FederatedTask { TaskId = Guid.NewGuid(), TreId = 1, TreName = "tre-a", StudyId = "study-1", TesTaskJson = "{}", SubmittedAt = At };
            queued.MoveTo(FederatedTaskPhase.Queued, At);
            open.Parts.Add(queued);
            _db.FederatedStudyVersions.Add(open);
            _db.SaveChanges();
            Assert.IsType<OkObjectResult>(await Studies("alice").Cancel(open.VersionId.ToString()));
            // A version whose aggregator report is still to come.
            var ready = new FederatedStudyVersion
            {
                VersionId = Guid.NewGuid(), StudyId = "study-2", Version = 1, Sites = "tre-a|tre-b", CodeSha256 = new string('c', 64),
                TesTaskJson = "{}", SubmittedAt = At, DeadlineAt = At.AddDays(7), Outcome = FederatedStudyOutcome.Failed, AggregationErrorCode = "run_failed"
            };
            _db.FederatedStudyVersions.Add(ready);
            _db.SaveChanges();

            await Aggregation("bob").RetryAggregation(ready.VersionId.ToString());
            await Aggregation("service-account-federated-aggregator").ReportAggregation(
                new FederatedAggregationReportForm { VersionId = ready.VersionId.ToString(), Phase = "failed", ErrorCode = "code_error" }, null);

            Assert.Equal(new[] { LogType.FederatedStudyCanceled, LogType.FederatedAggregationRetried },
                _db.AuditLogs.OrderBy(a => a.Id).Select(a => a.LogType));
            Assert.Equal(new[] { "alice", "bob" }, _db.AuditLogs.OrderBy(a => a.Id).Select(a => a.LoggedInUserName));
        }

        [Fact]
        public async Task Storing_the_pooled_result_writes_an_audit_entry_with_its_hash()
        {
            var version = new FederatedStudyVersion
            {
                VersionId = Guid.NewGuid(), StudyId = "study-3", Version = 1, Sites = "tre-a|tre-b", CodeSha256 = new string('c', 64),
                TesTaskJson = "{}", SubmittedAt = At, DeadlineAt = At.AddDays(7), Outcome = FederatedStudyOutcome.ReadyForAggregation, ReadyAt = At
            };
            foreach (var (treId, tre) in new[] { (1, "tre-a"), (2, "tre-b") })
            {
                var part = new FederatedTask { TaskId = Guid.NewGuid(), TreId = treId, TreName = tre, StudyId = "study-3", TesTaskJson = "{}", SubmittedAt = At };
                part.MoveTo(FederatedTaskPhase.Released, At);
                version.Parts.Add(part);
            }
            _db.FederatedStudyVersions.Add(version);
            _db.SaveChanges();
            var pooled = new FederatedPooledResultDocument
            {
                StudyId = "study-3", Version = 1, VersionId = version.VersionId, CodeSha256 = version.CodeSha256, Sites = 2, CompletedAt = At,
                Result = new FederatedPooledResult { N = 4, Value = JsonDocument.Parse("{\"n\":4}").RootElement.Clone() }
            }.Serialize();

            Assert.IsType<OkObjectResult>(await Aggregation("service-account-federated-aggregator").ReportAggregation(
                new FederatedAggregationReportForm { VersionId = version.VersionId.ToString(), Phase = "aggregated" },
                new FormFile(new MemoryStream(pooled), 0, pooled.Length, "result", "pooled.json")));

            var audit = Assert.Single(_db.AuditLogs);
            Assert.Equal(LogType.FederatedPooledResultStored, audit.LogType);
            Assert.Contains(FederatedResultDocumentJson.Sha256Hex(pooled), audit.HistoricFormData);
        }

        [Fact]
        public async Task GetStudyResult_returns_the_pooled_file_after_checking_its_hash()
        {
            var pooled = Encoding.UTF8.GetBytes("{\"pooled\":true}");
            var version = Seed(bReleased: true, pooled);

            var file = Assert.IsType<FileContentResult>(await Studies().GetStudyResult(version.VersionId.ToString()));

            Assert.Equal(pooled, file.FileContents);
            Assert.Equal("application/json", file.ContentType);
            Assert.Equal($"{version.VersionId}.json", file.FileDownloadName);
        }

        [Theory]
        [InlineData("incomplete", 404, "result_not_found")]
        [InlineData("changed", 500, "internal_error")]
        [InlineData("missing", 404, "result_not_found")]
        [InlineData("storage_down", 503, "storage_unavailable")]
        [InlineData("unknown", 404, "study_not_found")]
        public async Task GetStudyResult_refuses_when_there_is_no_good_pooled_file(string how, int status, string code)
        {
            var version = how == "incomplete" ? Seed(bReleased: false) : Seed(bReleased: true, Encoding.UTF8.GetBytes("{\"pooled\":true}"));
            if (how == "changed") _store.Objects[version.PooledResultKey!] = Encoding.UTF8.GetBytes("{}");
            if (how == "missing") _store.Objects.Remove(version.PooledResultKey!);
            if (how == "storage_down") _store.FailGets = true;

            AssertError(await Studies().GetStudyResult(how == "unknown" ? Guid.NewGuid().ToString() : version.VersionId.ToString()), status, code);
        }

        [Fact]
        public async Task GetStudyAudit_of_an_aggregated_version_shows_how_it_was_produced()
        {
            var version = Seed(bReleased: true, Encoding.UTF8.GetBytes("{\"pooled\":true}"));

            var audit = Assert.IsType<FederatedStudyAuditView>(Assert.IsType<OkObjectResult>(await Studies().GetStudyAudit(version.VersionId.ToString())).Value);

            Assert.Equal(("study-1", 1, version.VersionId, "control-admin", version.CodeSha256), (audit.StudyId, audit.Version, audit.VersionId, audit.SubmittedBy, audit.CodeSha256));
            Assert.Equal(("aggregated", version.PooledResultSha256), (audit.Outcome, audit.PooledResultSha256));
            Assert.Equal(At.AddMinutes(4), audit.AggregationStartedAt);
            Assert.Equal(new[] { "tre-a", "tre-b" }, audit.Hospitals.Select(h => h.Tre));
            Assert.Equal(new[] { "queued", "running", "released" }, audit.Hospitals[0].Phases.Select(p => p.Phase));
            Assert.Equal(At.AddMinutes(2), audit.Hospitals[0].Phases[2].At);
            Assert.Equal(new string('a', 64), audit.Hospitals[0].ResultSha256);
            Assert.EndsWith("Z\"", Newtonsoft.Json.JsonConvert.SerializeObject(audit.SubmittedAt));
        }

        [Fact]
        public async Task GetStudyAudit_of_an_incomplete_version_says_why_there_is_no_pooled_result()
        {
            var version = Seed(bReleased: false);

            var audit = Assert.IsType<FederatedStudyAuditView>(Assert.IsType<OkObjectResult>(await Studies().GetStudyAudit(version.VersionId.ToString())).Value);

            Assert.Equal(("incomplete", "minimum_unreachable", (string?)null), (audit.Outcome, audit.OutcomeReason, audit.PooledResultSha256));
            var rejected = audit.Hospitals.Single(h => h.Tre == "tre-b").Phases.Last();
            Assert.Equal(("rejected_at_output_approval", "rejected_by_reviewer"), (rejected.Phase, rejected.ErrorCode));
            AssertError(await Studies().GetStudyAudit(Guid.NewGuid().ToString()), 404, "study_not_found");
        }

        [Theory]
        [InlineData(nameof(FederatedTaskController.GetStudyResult))]
        [InlineData(nameof(FederatedTaskController.GetStudyAudit))]
        public void Study_reads_are_for_orchestrator_admins_only(string action)
        {
            Assert.Equal("dare-control-admin", typeof(FederatedTaskController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        }
    }
}
