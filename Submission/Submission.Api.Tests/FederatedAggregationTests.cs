using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
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
    public class FederatedAggregationTests
    {
        private static readonly DateTime Ready = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        private readonly ApplicationDbContext _db;
        private readonly FakeResultStore _store = new();

        public FederatedAggregationTests()
        {
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            _db.Tres.Add(new Tre { Id = 1, Name = "tre-a", AdminUsername = "a", KeycloakClientId = "tre-a-client", About = "", FormData = "" });
            _db.Tres.Add(new Tre { Id = 2, Name = "tre-b", AdminUsername = "b", KeycloakClientId = "tre-b-client", About = "", FormData = "" });
            _db.SaveChanges();
        }

        private FederatedAggregationController Controller() => new(_db, _store)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "someone")], "test"))
                }
            }
        };

        /// <summary>A version for tre-a and tre-b with the given outcome; each value released and stored, null for a rejected part.</summary>
        private FederatedStudyVersion Seed(FederatedStudyOutcome? outcome, string? valueA, string? valueB)
        {
            var version = new FederatedStudyVersion
            {
                VersionId = Guid.NewGuid(),
                StudyId = "study-1",
                Version = (_db.FederatedStudyVersions.Max(v => (int?)v.Version) ?? 0) + 1,
                Sites = "tre-a|tre-b",
                CodeSha256 = new string('c', 64),
                TesTaskJson = "{\"tags\":{\"fed.sites\":\"tre-a|tre-b\"}}",
                SubmittedAt = Ready.AddHours(-1),
                DeadlineAt = Ready.AddDays(6),
                Outcome = outcome,
                ReadyAt = outcome == FederatedStudyOutcome.ReadyForAggregation ? Ready : null
            };
            // tre-b's part is added first, so fed.sites order (not insertion order) must decide the results order.
            foreach (var (treId, tre, value) in new[] { (2, "tre-b", valueB), (1, "tre-a", valueA) })
            {
                var part = new FederatedTask
                {
                    TaskId = Guid.NewGuid(), TreId = treId, TreName = tre, StudyId = "study-1", Operation = "run-analysis",
                    Dataset = "cohort_2024", TesTaskJson = "{}", SubmittedAt = version.SubmittedAt
                };
                part.MoveTo(FederatedTaskPhase.Queued, version.SubmittedAt);
                part.MoveTo(FederatedTaskPhase.Running, version.SubmittedAt);
                if (value is null)
                {
                    part.Reject(FederatedTaskPhase.RejectedAtOutputApproval, version.SubmittedAt, FederatedTaskErrorCodes.RejectedByReviewer, "small cells");
                }
                else
                {
                    var bytes = FederatedResultDocumentJson.SerializeAnalysis(new FederatedAnalysisResultDocument
                    {
                        TaskId = part.TaskId.ToString(), Tre = tre, StudyId = "study-1", Operation = "run-analysis", Dataset = "cohort_2024",
                        DatasetSha256 = new string('d', 64), CompletedAt = Ready,
                        Result = new FederatedAnalysisResult { N = 1, Value = JsonDocument.Parse(value).RootElement.Clone() }
                    });
                    part.MoveTo(FederatedTaskPhase.Released, version.SubmittedAt);
                    part.ResultBucket = _store.Bucket;
                    part.ResultSha256 = FederatedResultDocumentJson.Sha256Hex(bytes);
                    part.ResultKey = FederatedResultKeys.For(treId, part.TaskId, part.ResultSha256);
                    _store.Objects[part.ResultKey] = bytes;
                }
                version.Parts.Add(part);
            }

            _db.FederatedStudyVersions.Add(version);
            _db.SaveChanges();
            return version;
        }

        private FederatedStudyVersion Reload(FederatedStudyVersion version)
        {
            _db.ChangeTracker.Clear();
            return _db.FederatedStudyVersions.Single(v => v.Id == version.Id);
        }

        private static List<FederatedAggregationJob> Jobs(IActionResult result) =>
            Assert.IsType<List<FederatedAggregationJob>>(Assert.IsType<OkObjectResult>(result).Value);

        private static void AssertError(IActionResult result, int status, string code)
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(status, obj.StatusCode);
            Assert.Equal(code, Assert.IsType<FederatedApiError>(obj.Value).ErrorCode);
        }

        private static byte[] Pooled(FederatedStudyVersion v, int sites = 2, long n = 4, string value = "{\"n\":4,\"mean_age\":35.0}") =>
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, studyId = v.StudyId, version = v.Version, versionId = v.VersionId, codeSha256 = v.CodeSha256,
                sites, completedAt = Ready, result = new { kind = "aggregate", n, value = JsonDocument.Parse(value).RootElement }
            }));

        private static IFormFile File(byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, FederatedReportFields.ResultFile, "pooled.json");

        private Task<IActionResult> Report(FederatedStudyVersion v, string phase, string? errorCode = null, byte[]? file = null, int? errorLine = null) =>
            Controller().ReportAggregation(new FederatedAggregationReportForm { VersionId = v.VersionId.ToString(), Phase = phase, ErrorCode = errorCode, ErrorLine = errorLine },
                file is null ? null : File(file));

        /// <summary>
        /// The line of the researcher's file that failed helps them fix aggregate(); it's a number, so no text from the code
        /// reaches Submission. Only code_error takes one.
        /// </summary>
        [Fact]
        public async Task Code_error_keeps_the_line_number_and_nothing_else()
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");
            var other = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");

            Assert.IsType<OkObjectResult>(await Report(version, "failed", "code_error", errorLine: 7));
            AssertError(await Report(other, "failed", "run_failed", errorLine: 7), 400, "invalid_task");
            AssertError(await Report(other, "failed", "code_error", errorLine: 0), 400, "invalid_task");

            Assert.EndsWith("(analysis.py line 7)", Reload(version).OutcomeMessage);
        }

        [Fact]
        public async Task GetPending_hands_out_ready_versions_with_values_only()
        {
            var ready = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3,\"mean_age\":40}", "{\"n\":1,\"mean_age\":20}");
            Seed(null, "{\"n\":3}", "{\"n\":1}");
            Seed(FederatedStudyOutcome.Incomplete, "{\"n\":3}", null);
            Seed(FederatedStudyOutcome.Aggregated, "{\"n\":3}", "{\"n\":1}");

            var job = Assert.Single(Jobs(await Controller().GetPendingAggregations()));

            Assert.Equal(ready.VersionId, job.VersionId);
            Assert.Equal(("study-1", ready.Version, ready.CodeSha256, ready.TesTaskJson), (job.StudyId, job.Version, job.CodeSha256, job.TesTaskJson));
            Assert.Equal("[{\"n\":3,\"mean_age\":40},{\"n\":1,\"mean_age\":20}]", job.ResultsJson);
            // aggregate() sees only ResultsJson. (The task in TesTaskJson names the hospitals; the aggregator reads the code,
            // image and limits from it and never stages it.)
            Assert.DoesNotContain("tre-", job.ResultsJson);
            Assert.DoesNotContain(ready.Parts[0].TaskId.ToString(), Newtonsoft.Json.JsonConvert.SerializeObject(job));
        }

        [Theory]
        [InlineData("changed")]
        [InlineData("missing")]
        public async Task GetPending_fails_a_version_whose_site_file_cannot_be_read_back(string how)
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");
            var key = version.Parts.First(p => p.TreName == "tre-a").ResultKey!;
            if (how == "changed") _store.Objects[key] = Encoding.UTF8.GetBytes("{}");
            else _store.Objects.Remove(key);

            Assert.Empty(Jobs(await Controller().GetPendingAggregations()));

            var stored = Reload(version);
            Assert.Equal((FederatedStudyOutcome.Failed, "internal_error"), (stored.Outcome, stored.AggregationErrorCode));
        }

        [Fact]
        public async Task GetPending_skips_a_version_when_storage_is_down()
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");
            _store.FailGets = true;

            Assert.Empty(Jobs(await Controller().GetPendingAggregations()));

            Assert.Equal(FederatedStudyOutcome.ReadyForAggregation, Reload(version).Outcome);
        }

        [Fact]
        public async Task Running_records_the_first_start_only()
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");

            Assert.IsType<OkObjectResult>(await Report(version, "running"));
            var first = Reload(version).AggregationStartedAt;
            Assert.IsType<OkObjectResult>(await Report(version, "running"));

            Assert.NotNull(first);
            Assert.Equal(first, Reload(version).AggregationStartedAt);
        }

        [Fact]
        public async Task Aggregated_stores_the_pooled_result()
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");
            var bytes = Pooled(version);

            Assert.IsType<OkObjectResult>(await Report(version, "aggregated", file: bytes));

            var stored = Reload(version);
            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
            Assert.Equal(FederatedStudyOutcome.Aggregated, stored.Outcome);
            // No running report came first (it was lost, or a retry collected an earlier run): the audit trail still has a start.
            Assert.NotNull(stored.AggregationStartedAt);
            Assert.Equal((_store.Bucket, $"studies/{version.VersionId:D}/{sha}.json", sha), (stored.PooledResultBucket, stored.PooledResultKey, stored.PooledResultSha256));
            Assert.Equal(bytes, _store.Objects[stored.PooledResultKey!]);

            // The identical report again (a lost response) changes nothing.
            Assert.IsType<OkObjectResult>(await Report(version, "aggregated", file: bytes));
        }

        [Theory]
        [InlineData("versionId")]
        [InlineData("sites")]
        [InlineData("n")]
        [InlineData("kind")]
        [InlineData("none")]
        public async Task Aggregated_with_a_document_that_does_not_match_is_refused(string wrong)
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");
            var other = new FederatedStudyVersion { VersionId = Guid.NewGuid(), StudyId = version.StudyId, Version = version.Version, CodeSha256 = version.CodeSha256 };
            byte[]? bytes = wrong switch
            {
                "versionId" => Pooled(other),
                "sites" => Pooled(version, sites: 3),
                "n" => Pooled(version, n: 5),
                "kind" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Pooled(version)).Replace("\"aggregate\"", "\"analysis\"")),
                _ => null
            };

            var result = await Report(version, "aggregated", file: bytes);

            AssertError(result, 400, wrong == "none" ? "invalid_task" : "invalid_result");
            Assert.Equal(FederatedStudyOutcome.ReadyForAggregation, Reload(version).Outcome);
            Assert.DoesNotContain(_store.Objects.Keys, k => k.StartsWith("studies/"));
        }

        [Theory]
        [InlineData("invalid_task")]
        [InlineData("image_not_approved")]
        [InlineData("unsafe_code")]
        [InlineData("code_error")]
        [InlineData("invalid_result")]
        [InlineData("result_too_large")]
        [InlineData("timeout")]
        [InlineData("out_of_memory")]
        [InlineData("run_failed")]
        [InlineData("internal_error")]
        public async Task Failed_records_the_code_with_a_fixed_message(string code)
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");

            Assert.IsType<OkObjectResult>(await Report(version, "failed", code));

            var stored = Reload(version);
            Assert.Equal((FederatedStudyOutcome.Failed, code), (stored.Outcome, stored.AggregationErrorCode));
            Assert.NotNull(stored.AggregationStartedAt);
            Assert.False(string.IsNullOrWhiteSpace(stored.OutcomeMessage));
        }

        [Theory]
        [InlineData("failed", "boom")]
        [InlineData("failed", null)]
        [InlineData("aggregation_timeout", null)]
        [InlineData("done", null)]
        public async Task Bad_reports_are_refused(string phase, string? code)
        {
            var version = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");

            AssertError(await Report(version, phase, code), 400, "invalid_task");
            Assert.Equal(FederatedStudyOutcome.ReadyForAggregation, Reload(version).Outcome);
        }

        [Fact]
        public async Task Reports_on_a_version_that_is_not_ready_are_refused()
        {
            var incomplete = Seed(FederatedStudyOutcome.Incomplete, "{\"n\":3}", null);
            var timedOut = Seed(FederatedStudyOutcome.ReadyForAggregation, "{\"n\":3}", "{\"n\":1}");
            FederatedStudyOutcomes.TimeOutAggregation(timedOut, Ready.AddDays(2), 1440);
            _db.SaveChanges();

            AssertError(await Report(incomplete, "running"), 409, "task_already_finished");
            // A pooled result that arrives after the timeout is not taken: the admin retries instead.
            AssertError(await Report(timedOut, "aggregated", file: Pooled(timedOut)), 409, "task_already_finished");
            Assert.DoesNotContain(_store.Objects.Keys, k => k.StartsWith("studies/"));
            AssertError(await Controller().ReportAggregation(new FederatedAggregationReportForm { VersionId = Guid.NewGuid().ToString(), Phase = "running" }, null),
                404, "study_not_found");
        }

        [Theory]
        [InlineData(FederatedStudyOutcome.Failed, "aggregation_timeout", 200)]
        [InlineData(FederatedStudyOutcome.Failed, "run_failed", 200)]
        [InlineData(FederatedStudyOutcome.Failed, "internal_error", 200)]
        [InlineData(FederatedStudyOutcome.Failed, "code_error", 409)]
        [InlineData(FederatedStudyOutcome.Aggregated, null, 409)]
        [InlineData(FederatedStudyOutcome.ReadyForAggregation, null, 409)]
        public async Task Retry_reopens_only_infrastructure_failures(FederatedStudyOutcome outcome, string? code, int status)
        {
            var version = Seed(outcome, "{\"n\":3}", "{\"n\":1}");
            version.AggregationErrorCode = code;
            version.AggregationStartedAt = Ready;
            version.ReadyAt = Ready;
            _db.SaveChanges();

            var result = await Controller().RetryAggregation(version.VersionId.ToString());

            var stored = Reload(version);
            if (status == 200)
            {
                Assert.IsType<OkObjectResult>(result);
                Assert.Equal(FederatedStudyOutcome.ReadyForAggregation, stored.Outcome);
                Assert.Null(stored.AggregationErrorCode);
                Assert.Null(stored.AggregationStartedAt);
                Assert.True(stored.ReadyAt > Ready);
            }
            else
            {
                AssertError(result, 409, "task_already_finished");
                Assert.Equal(outcome, stored.Outcome);
            }
        }

        [Fact]
        public async Task Retry_of_an_unknown_version_is_404()
        {
            AssertError(await Controller().RetryAggregation(Guid.NewGuid().ToString()), 404, "study_not_found");
        }

        [Theory]
        [InlineData(nameof(FederatedAggregationController.GetPendingAggregations), "dare-federated-aggregator")]
        [InlineData(nameof(FederatedAggregationController.ReportAggregation), "dare-federated-aggregator")]
        [InlineData(nameof(FederatedAggregationController.RetryAggregation), "dare-control-admin")]
        public void Routes_need_their_role(string action, string role)
        {
            var authorize = typeof(FederatedAggregationController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();

            Assert.Equal(role, authorize!.Roles);
        }
    }
}
