using System.Reflection;
using System.Security.Claims;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Submission.Api.Controllers;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Models;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedTaskControllerTests
    {
        private static ApplicationDbContext NewDb()
        {
            var db = OpenDb(Guid.NewGuid().ToString());
            db.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            db.Tres.Add(NewTre(2, "tre-b", "tre-b-admin", "tre-b-client"));
            db.SaveChanges();
            return db;
        }

        // A second context on the same database stands in for a second request.
        private static ApplicationDbContext OpenDb(string name) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options);

        private static Tre NewTre(int id, string name, string admin, string clientId) => new()
        {
            Id = id,
            Name = name,
            AdminUsername = admin,
            KeycloakClientId = clientId,
            About = "",
            FormData = ""
        };

        private static FederatedTaskController ControllerFor(ApplicationDbContext db, string username, FakeResultStore? store = null)
        {
            var identity = new ClaimsIdentity([new Claim("preferred_username", username)], "test");
            return new FederatedTaskController(db, store ?? new FakeResultStore())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
                }
            };
        }

        private static FederatedTaskController AgentControllerFor(ApplicationDbContext db, string clientId, FakeResultStore? store = null) =>
            ControllerWithClaims(db, store, new Claim("azp", clientId), new Claim("preferred_username", "service-account-" + clientId));

        private static FederatedTaskController ControllerWithClaims(ApplicationDbContext db, params Claim[] claims) =>
            ControllerWithClaims(db, null, claims);

        private static FederatedTaskController ControllerWithClaims(ApplicationDbContext db, FakeResultStore? store, params Claim[] claims) =>
            new(db, store ?? new FakeResultStore())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
                }
            };

        private static FederatedApiError AssertError(IActionResult result, int status, string code)
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(status, obj.StatusCode);
            var error = Assert.IsType<FederatedApiError>(obj.Value);
            Assert.Equal(code, error.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(error.ErrorMessage));
            return error;
        }

        private static TesTask MeanTask(string tre = "tre-a", string dataset = "cohort_2024") =>
            FederatedTesProfile.Build(new FederatedTaskSpec(
                "mean", dataset, "age", [], "study-1", tre));

        private static FederatedTask SeedTask(ApplicationDbContext db, int treId, TesState state, TesTask? task = null, string? treName = null)
        {
            var entity = new FederatedTask
            {
                TaskId = Guid.NewGuid(),
                TreId = treId,
                TreName = treName ?? (treId == 1 ? "tre-a" : "tre-b"),
                StudyId = "study-1",
                Operation = "mean",
                Dataset = "cohort_2024",
                TesTaskJson = JsonConvert.SerializeObject(task ?? MeanTask()),
                State = state,
                SubmittedAt = DateTime.UtcNow
            };
            db.FederatedTasks.Add(entity);
            db.SaveChanges();
            return entity;
        }

        [Fact]
        public async Task Enqueue_Valid_PersistsQueuedTask()
        {
            using var db = NewDb();
            var result = await ControllerFor(db, "dare-admin").Enqueue(MeanTask());

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<TesCreateTaskResponse>(ok.Value);
            Assert.True(Guid.TryParse(response.Id, out var id));
            var row = db.FederatedTasks.Single();
            Assert.Equal(id, row.TaskId);
            Assert.Equal(TesState.QUEUEDEnum, row.State);
            Assert.Equal(1, row.TreId);
            Assert.Equal("tre-a", row.TreName);
            Assert.Equal("cohort_2024", row.Dataset);
        }

        [Fact]
        public async Task Enqueue_StoresDefinedStateInTaskJson()
        {
            using var db = NewDb();
            var task = MeanTask();
            task.State = TesState.COMPLETEEnum;

            await ControllerFor(db, "dare-admin").Enqueue(task);

            Assert.Contains("\"state\":\"UNKNOWN\"", db.FederatedTasks.Single().TesTaskJson);
        }

        [Fact]
        public void TreDelete_IsRestrictedByFederatedTasks()
        {
            using var db = NewDb();
            var fk = db.Model.FindEntityType(typeof(FederatedTask))!.GetForeignKeys()
                .Single(x => x.PrincipalEntityType.ClrType == typeof(Tre));

            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        }

        public static IEnumerable<object[]> OutsideProfile()
        {
            var image = MeanTask();
            image.Executors[0].Image = "ubuntu:22.04";
            yield return [image];

            var multi = MeanTask();
            multi.Tags!["tres"] = "tre-a|tre-b";
            yield return [multi];

            var unknown = MeanTask();
            unknown.Tags!["tres"] = "tre-zzz";
            yield return [unknown];

            var output = MeanTask();
            output.Outputs = [new TesOutput { Url = "s3://x/y", Path = "/out" }];
            yield return [output];
        }

        [Theory]
        [MemberData(nameof(OutsideProfile))]
        public async Task Enqueue_RejectsOutsideProfile(TesTask task)
        {
            using var db = NewDb();
            var result = await ControllerFor(db, "dare-admin").Enqueue(task);

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Equal(0, db.FederatedTasks.Count());
        }

        [Fact]
        public async Task Enqueue_TreLookupIsCaseInsensitive()
        {
            using var db = NewDb();
            var task = MeanTask();
            task.Tags!["tres"] = "TRE-A";
            var result = await ControllerFor(db, "dare-admin").Enqueue(task);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(1, db.FederatedTasks.Single().TreId);
        }

        [Fact]
        public async Task Enqueue_StripsServerOwnedFields()
        {
            using var db = NewDb();
            var task = MeanTask();
            task.Id = "spoofed";
            task.State = TesState.COMPLETEEnum;
            task.CreationTime = DateTimeOffset.UtcNow.AddDays(-1);
            task.Logs = [new TesTaskLog { Metadata = new Dictionary<string, string> { ["value"] = "999" } }];

            await ControllerFor(db, "dare-admin").Enqueue(task);

            var pending = Assert.IsType<OkObjectResult>(
                await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre());
            var returned = Assert.Single(Assert.IsType<List<TesTask>>(pending.Value));
            Assert.Null(returned.Logs);
            Assert.Equal(TesState.QUEUEDEnum, returned.State);
            Assert.NotEqual("spoofed", returned.Id);
        }

        [Fact]
        public async Task GetPending_accepts_tre_service_account()
        {
            using var db = NewDb();
            var mine = SeedTask(db, 1, TesState.QUEUEDEnum);
            SeedTask(db, 2, TesState.QUEUEDEnum);

            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var task = Assert.Single(Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value));
            Assert.Equal(mine.TaskId.ToString(), task.Id);
        }

        [Fact]
        public async Task GetPending_rejects_tre_admin_username()
        {
            using var db = NewDb();
            SeedTask(db, 1, TesState.QUEUEDEnum);

            var result = await ControllerWithClaims(db, new Claim("preferred_username", "tre-a-admin")).GetPendingTasksForTre();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetPending_rejects_username_without_matching_azp()
        {
            using var db = NewDb();
            SeedTask(db, 1, TesState.QUEUEDEnum);

            var result = await ControllerWithClaims(db,
                new Claim("azp", "other"),
                new Claim("preferred_username", "service-account-tre-a-client")).GetPendingTasksForTre();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetPending_ReturnsOnlyCallersQueuedTasks()
        {
            using var db = NewDb();
            var mine = SeedTask(db, 1, TesState.QUEUEDEnum);
            SeedTask(db, 1, TesState.COMPLETEEnum);
            SeedTask(db, 2, TesState.QUEUEDEnum);

            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var ok = Assert.IsType<OkObjectResult>(result);
            var task = Assert.Single(Assert.IsType<List<TesTask>>(ok.Value));
            Assert.Equal(mine.TaskId.ToString(), task.Id);
            Assert.Equal(TesState.QUEUEDEnum, task.State);
            Assert.Equal("mean", task.Executors![0].Command![0]);
        }

        [Fact]
        public async Task GetPending_SkipsTasksEnqueuedUnderAnOldTreName()
        {
            using var db = NewDb();
            for (var i = 0; i < 60; i++)
            {
                SeedTask(db, 1, TesState.QUEUEDEnum, treName: "old-tre-a");
            }
            var current = SeedTask(db, 1, TesState.QUEUEDEnum);

            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var task = Assert.Single(Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value));
            Assert.Equal(current.TaskId.ToString(), task.Id);
        }

        private static Dictionary<string, string> Fields(Guid taskId, string state, string? errorCode = null, string? errorMessage = null, string? datasetSha256 = null)
        {
            var fields = new Dictionary<string, string>
            {
                [FederatedReportFields.TaskId] = taskId.ToString(),
                [FederatedReportFields.State] = state
            };
            if (errorCode != null) fields[FederatedReportFields.ErrorCode] = errorCode;
            if (errorMessage != null) fields[FederatedReportFields.ErrorMessage] = errorMessage;
            if (datasetSha256 != null) fields[FederatedReportFields.DatasetSha256] = datasetSha256;
            return fields;
        }

        private static FederatedTask SeededTask(ApplicationDbContext db, int treId = 1) => SeedTask(db, treId, TesState.QUEUEDEnum);

        /// <summary>A valid result document for the task, as the Agent would write it.</summary>
        private static byte[] ValidResult(FederatedTask task, Action<FederatedResultDocument>? tweak = null)
        {
            var doc = new FederatedResultDocument
            {
                TaskId = task.TaskId.ToString(),
                Tre = task.TreName,
                StudyId = task.StudyId,
                Operation = task.Operation,
                Dataset = task.Dataset,
                DatasetSha256 = new string('b', 64),
                CompletedAt = DateTimeOffset.UtcNow,
                Result = new FederatedScalarResult { Kind = "scalar", Value = 40.5m, RowCount = 2 }
            };
            tweak?.Invoke(doc);
            return FederatedResultDocumentJson.Serialize(doc);
        }

        /// <summary>Calls ReportResult as a multipart request: the text fields, plus the given files as file parts.</summary>
        private static Task<IActionResult> ReportAs(FederatedTaskController controller, Dictionary<string, string> fields,
            params (string Name, string FileName, byte[] Content)[] files)
        {
            var formFiles = new FormFileCollection();
            foreach (var (name, fileName, content) in files)
            {
                formFiles.Add(new FormFile(new MemoryStream(content), 0, content.Length, name, fileName));
            }

            controller.ControllerContext.HttpContext.Request.Form =
                new FormCollection(fields.ToDictionary(x => x.Key, x => new Microsoft.Extensions.Primitives.StringValues(x.Value)), formFiles);

            var form = new FederatedTaskReportForm
            {
                TaskId = fields.GetValueOrDefault(FederatedReportFields.TaskId),
                State = fields.GetValueOrDefault(FederatedReportFields.State),
                ErrorCode = fields.GetValueOrDefault(FederatedReportFields.ErrorCode),
                ErrorMessage = fields.GetValueOrDefault(FederatedReportFields.ErrorMessage),
                DatasetSha256 = fields.GetValueOrDefault(FederatedReportFields.DatasetSha256)
            };
            return controller.ReportResult(form);
        }

        private static Task<IActionResult> ReportComplete(FederatedTaskController controller, FederatedTask task, byte[]? content = null, string fileName = "result.json") =>
            ReportAs(controller, Fields(task.TaskId, "COMPLETE"), (FederatedReportFields.ResultFile, fileName, content ?? ValidResult(task)));

        private static async Task<TesTask> GetTask(ApplicationDbContext db, Guid id)
        {
            var result = await ControllerFor(db, "dare-admin").GetTask(id.ToString());
            return Assert.IsType<TesTask>(Assert.IsType<OkObjectResult>(result).Value);
        }

        [Fact]
        public async Task Report_complete_stores_file_and_returns_location()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded);
            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded, bytes);

            var response = Assert.IsType<FederatedReportResponse>(Assert.IsType<OkObjectResult>(result).Value);
            var key = $"1/{seeded.TaskId}/{sha}.json";
            Assert.True(response.Result);
            Assert.Equal("federated-results", response.ResultBucket);
            Assert.Equal(key, response.ResultKey);
            Assert.Equal(sha, response.ResultSha256);
            var stored = Assert.Single(store.Objects);
            Assert.Equal(key, stored.Key);
            Assert.Equal(bytes, stored.Value);
            var row = db.FederatedTasks.AsNoTracking().Single();
            Assert.Equal(TesState.COMPLETEEnum, row.State);
            Assert.Equal("federated-results", row.ResultBucket);
            Assert.Equal(key, row.ResultKey);
            Assert.Equal(sha, row.ResultSha256);
            Assert.Equal(new string('b', 64), row.DatasetSha256);
            Assert.NotNull(row.CompletedAt);
        }

        [Fact]
        public async Task Report_complete_then_GetTask_shows_result_location()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded);

            await ReportComplete(AgentControllerFor(db, "tre-a-client"), seeded, bytes);

            var task = await GetTask(db, seeded.TaskId);
            Assert.Equal(TesState.COMPLETEEnum, task.State);
            var metadata = task.Logs![0].Metadata;
            Assert.Equal("federated-results", metadata["result_bucket"]);
            Assert.Equal(FederatedResultDocumentJson.Sha256Hex(bytes), metadata["result_sha256"]);
            Assert.Equal(new string('b', 64), metadata["dataset_sha256"]);
        }

        [Fact]
        public async Task Report_complete_accepts_document_for_mixed_case_tre_name()
        {
            using var db = NewDb();
            db.Tres.Single(x => x.Id == 1).Name = "Tre-A";
            db.SaveChanges();
            var seeded = SeededTask(db);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client"), seeded);

            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task Report_complete_storage_failure_leaves_task_queued()
        {
            using var db = NewDb();
            var store = new FakeResultStore { FailPuts = true };
            var seeded = SeededTask(db);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded);

            var error = AssertError(result, 503, FederatedTaskErrorCodes.StorageUnavailable);
            Assert.DoesNotContain("storage down", error.ErrorMessage);
            var row = db.FederatedTasks.AsNoTracking().Single();
            Assert.Equal(TesState.QUEUEDEnum, row.State);
            Assert.Null(row.ResultBucket);
            Assert.Null(row.ResultKey);
            Assert.Null(row.ResultSha256);
            Assert.Null(row.CompletedAt);
        }

        [Fact]
        public async Task Report_complete_too_large_returns_result_too_large()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded, new byte[FederatedResultLimits.MaxResultBytes + 1]);

            AssertError(result, 400, FederatedTaskErrorCodes.ResultTooLarge);
            Assert.Empty(store.Objects);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_complete_invalid_document_returns_invalid_result()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded, "{}"u8.ToArray());

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidResult);
            Assert.Empty(store.Objects);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_complete_identity_mismatch_returns_invalid_result()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded, d => d.TaskId = Guid.NewGuid().ToString());

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded, bytes);

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidResult);
            Assert.Empty(store.Objects);
        }

        [Fact]
        public async Task Report_complete_without_file_is_invalid_task()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "COMPLETE"));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_complete_with_extra_file_part_is_invalid_task()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client", store), Fields(seeded.TaskId, "COMPLETE"),
                (FederatedReportFields.ResultFile, "result.json", bytes), ("other", "other.json", bytes));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Empty(store.Objects);
        }

        [Fact]
        public async Task Report_complete_with_wrongly_named_file_part_is_invalid_task()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "COMPLETE"),
                ("file", "result.json", ValidResult(seeded)));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
        }

        [Fact]
        public async Task Report_complete_with_dataset_sha_field_is_invalid_task()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"),
                Fields(seeded.TaskId, "COMPLETE", datasetSha256: new string('c', 64)),
                (FederatedReportFields.ResultFile, "result.json", ValidResult(seeded)));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
        }

        [Fact]
        public async Task Report_complete_ignores_client_file_name()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded, bytes, "../../evil.json");

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal($"1/{seeded.TaskId}/{FederatedResultDocumentJson.Sha256Hex(bytes)}.json", Assert.Single(store.Objects).Key);
        }

        [Fact]
        public async Task Report_error_with_file_is_invalid_task()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client", store),
                Fields(seeded.TaskId, "EXECUTOR_ERROR", "dataset_not_found"),
                (FederatedReportFields.ResultFile, "result.json", ValidResult(seeded)));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Empty(store.Objects);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_error_writes_no_file()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);
            var hash = new string('0', 63) + "f";

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client", store),
                Fields(seeded.TaskId, "EXECUTOR_ERROR", "dataset_not_found", "no such dataset", hash));

            var response = Assert.IsType<FederatedReportResponse>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.True(response.Result);
            Assert.Null(response.ResultKey);
            Assert.Empty(store.Objects);
            var row = db.FederatedTasks.AsNoTracking().Single();
            Assert.Equal(TesState.EXECUTORERROREnum, row.State);
            Assert.Equal("dataset_not_found", row.ErrorCode);
            Assert.Equal("no such dataset", row.ResultErrorMessage);
            Assert.Equal(hash, row.DatasetSha256);
            Assert.Null(row.ResultKey);
            var task = await GetTask(db, seeded.TaskId);
            Assert.Equal("dataset_not_found", task.Logs![0].Metadata["error_code"]);
        }

        [Theory]
        [InlineData("Dataset-Not-Found")]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("a_very_long_code_a_very_long_code_a_very_long_code_a_very_long_code_x")]
        public async Task Report_error_rejects_bad_error_code(string code)
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "EXECUTOR_ERROR", code));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_error_without_error_code_is_invalid_task()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "SYSTEM_ERROR"));

            var error = AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Contains("ErrorCode", error.ErrorMessage);
        }

        [Theory]
        [InlineData(513)]
        [InlineData(-1)]
        public async Task Report_error_rejects_long_or_control_message(int length)
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            var message = length < 0 ? "a\u0000b" : new string('x', length);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "EXECUTOR_ERROR", "internal_error", message));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
        [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
        public async Task Report_error_rejects_bad_dataset_sha256(string hash)
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "EXECUTOR_ERROR", "internal_error", datasetSha256: hash));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_other_tres_task_returns_404_and_stores_nothing()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);

            var result = await ReportComplete(AgentControllerFor(db, "tre-b-client", store), seeded);

            AssertError(result, 404, FederatedTaskErrorCodes.TaskNotFound);
            Assert.Empty(store.Objects);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Fact]
        public async Task Report_already_errored_returns_409_without_sha()
        {
            using var db = NewDb();
            var seeded = SeedTask(db, 1, TesState.EXECUTORERROREnum);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), Fields(seeded.TaskId, "SYSTEM_ERROR", "internal_error"));

            var error = AssertError(result, 409, FederatedTaskErrorCodes.TaskAlreadyReported);
            Assert.Null(error.ResultSha256);
            Assert.Null(error.ResultKey);
        }

        [Fact]
        public async Task ReportResult_AlreadyReported_Returns409_AndFirstResultKept()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            var store = new FakeResultStore();
            var controller = AgentControllerFor(db, "tre-a-client", store);
            var first = ValidResult(seeded);
            await ReportComplete(controller, seeded, first);

            var second = await ReportComplete(controller, seeded, ValidResult(seeded, d => d.Result.Value = 99m));

            var error = AssertError(second, 409, FederatedTaskErrorCodes.TaskAlreadyReported);
            Assert.Single(store.Objects);
            var firstSha = FederatedResultDocumentJson.Sha256Hex(first);
            Assert.Equal(firstSha, error.ResultSha256);
            Assert.Equal(db.FederatedTasks.AsNoTracking().Single().ResultKey, error.ResultKey);
            Assert.NotNull(error.ResultKey);
            Assert.Equal(firstSha, db.FederatedTasks.AsNoTracking().Single().ResultSha256);
        }

        [Fact]
        public async Task ReportResult_ConcurrentReports_SecondReturns409_AndFirstKept()
        {
            var name = Guid.NewGuid().ToString();
            using var setup = OpenDb(name);
            setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            setup.SaveChanges();
            var seeded = SeedTask(setup, 1, TesState.QUEUEDEnum);

            // Both requests load the task while it is QUEUED, before either saves.
            using var dbA = OpenDb(name);
            using var dbB = OpenDb(name);
            dbA.FederatedTasks.Single();
            dbB.FederatedTasks.Single();

            var bytes = ValidResult(seeded);
            var first = await ReportComplete(AgentControllerFor(dbA, "tre-a-client"), seeded, bytes);
            var second = await ReportAs(AgentControllerFor(dbB, "tre-a-client"), Fields(seeded.TaskId, "SYSTEM_ERROR", "internal_error"));

            Assert.IsType<OkObjectResult>(first);
            AssertError(second, 409, FederatedTaskErrorCodes.TaskAlreadyReported);
            using var check = OpenDb(name);
            var row = check.FederatedTasks.Single();
            Assert.Equal(TesState.COMPLETEEnum, row.State);
            Assert.Equal(FederatedResultDocumentJson.Sha256Hex(bytes), row.ResultSha256);
            Assert.Null(row.ErrorCode);
        }

        [Fact]
        public async Task ReportResult_ConcurrentCompleteReports_SecondReturns409WithWinnersSha()
        {
            var name = Guid.NewGuid().ToString();
            using var setup = OpenDb(name);
            setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            setup.SaveChanges();
            var seeded = SeedTask(setup, 1, TesState.QUEUEDEnum);
            using var dbA = OpenDb(name);
            using var dbB = OpenDb(name);
            dbA.FederatedTasks.Single();
            dbB.FederatedTasks.Single();

            var winner = ValidResult(seeded);
            await ReportComplete(AgentControllerFor(dbA, "tre-a-client"), seeded, winner);
            var second = await ReportComplete(AgentControllerFor(dbB, "tre-a-client"), seeded, ValidResult(seeded, d => d.Result.Value = 7m));

            var error = AssertError(second, 409, FederatedTaskErrorCodes.TaskAlreadyReported);
            Assert.Equal(FederatedResultDocumentJson.Sha256Hex(winner), error.ResultSha256);
            Assert.Equal(Submission.Api.Services.FederatedResultKeys.For(1, seeded.TaskId, FederatedResultDocumentJson.Sha256Hex(winner)), error.ResultKey);
        }

        [Fact]
        public async Task Report_from_non_agent_missing_task_id_and_state_is_forbidden()
        {
            using var db = NewDb();
            var controller = ControllerWithClaims(db, new Claim("preferred_username", "tre-a-admin"));
            controller.ControllerContext.HttpContext.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());

            var result = await controller.ReportResult(new FederatedTaskReportForm { TaskId = null, State = null });

            Assert.IsType<ForbidResult>(result);
            var nullability = new NullabilityInfoContext();
            Assert.Equal(NullabilityState.Nullable, nullability.Create(typeof(FederatedTaskReportForm).GetProperty(nameof(FederatedTaskReportForm.TaskId))!).WriteState);
            Assert.Equal(NullabilityState.Nullable, nullability.Create(typeof(FederatedTaskReportForm).GetProperty(nameof(FederatedTaskReportForm.State))!).WriteState);
        }

        [Fact]
        public async Task Report_rejects_tre_admin_username()
        {
            using var db = NewDb();
            var store = new FakeResultStore();
            var seeded = SeededTask(db);

            var result = await ReportComplete(ControllerWithClaims(db, store, new Claim("preferred_username", "tre-a-admin")), seeded);

            Assert.IsType<ForbidResult>(result);
            Assert.Empty(store.Objects);
            Assert.Equal(TesState.QUEUEDEnum, db.FederatedTasks.AsNoTracking().Single().State);
        }

        [Theory]
        [InlineData("not-a-guid", "COMPLETE")]
        [InlineData("00000000-0000-0000-0000-000000000001", "RUNNING")]
        [InlineData("00000000-0000-0000-0000-000000000001", "complete")]
        [InlineData("00000000-0000-0000-0000-000000000001", "")]
        public async Task ReportResult_BadInput_Returns400(string taskId, string state)
        {
            using var db = NewDb();
            var fields = new Dictionary<string, string>
            {
                [FederatedReportFields.TaskId] = taskId,
                [FederatedReportFields.State] = state,
                [FederatedReportFields.ErrorCode] = "internal_error"
            };

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), fields);

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
        }

        [Theory]
        [InlineData("azp", "", "service-account-tre-a-client", false)]
        [InlineData(null, null, "service-account-tre-a-client", false)]
        [InlineData("azp", "", "service-account-", false)]
        [InlineData("client_id", "tre-a-client", "service-account-tre-a-client", true)]
        [InlineData("azp", "tre-a-client", "SERVICE-ACCOUNT-TRE-A-CLIENT", true)]
        [InlineData("azp", "TRE-A-CLIENT", "service-account-tre-a-client", false)]
        public async Task GetPending_agent_identity_edge_cases(string? claimType, string? azp, string username, bool accepted)
        {
            using var db = NewDb();
            db.Tres.Add(NewTre(3, "tre-c", "tre-c-admin", ""));
            db.SaveChanges();
            var claims = new List<Claim> { new("preferred_username", username) };
            if (claimType != null)
            {
                claims.Add(new Claim(claimType, azp!));
            }

            var result = await ControllerWithClaims(db, claims.ToArray()).GetPendingTasksForTre();

            if (accepted)
            {
                Assert.IsType<OkObjectResult>(result);
            }
            else
            {
                Assert.IsType<ForbidResult>(result);
            }
        }

        [Fact]
        public async Task GetTask_Unknown_Returns404()
        {
            using var db = NewDb();
            var result = await ControllerFor(db, "dare-admin").GetTask(Guid.NewGuid().ToString());
            AssertError(result, 404, FederatedTaskErrorCodes.TaskNotFound);
        }

        private static async Task<(FederatedTask Task, FakeResultStore Store, byte[] Bytes)> CompletedTask(ApplicationDbContext db)
        {
            var store = new FakeResultStore();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded);
            await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded, bytes);
            return (seeded, store, bytes);
        }

        [Fact]
        public async Task GetTaskResult_returns_stored_bytes_as_attachment()
        {
            using var db = NewDb();
            var (task, store, bytes) = await CompletedTask(db);
            var controller = ControllerFor(db, "dare-admin", store);

            var result = await controller.GetTaskResult(task.TaskId.ToString());

            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("application/json", file.ContentType);
            Assert.Equal($"{task.TaskId}.json", file.FileDownloadName);
            Assert.Equal(bytes, file.FileContents);
            Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        }

        [Fact]
        public async Task GetTaskResult_reads_from_the_bucket_recorded_on_the_task()
        {
            using var db = NewDb();
            var (task, store, _) = await CompletedTask(db);
            var row = db.FederatedTasks.Single(x => x.TaskId == task.TaskId);
            row.ResultBucket = "old-results";
            db.SaveChanges();

            await ControllerFor(db, "dare-admin", store).GetTaskResult(task.TaskId.ToString());

            Assert.Equal("old-results", store.LastGetBucket);
        }

        [Fact]
        public async Task GetTaskResult_unknown_task_404_task_not_found()
        {
            using var db = NewDb();
            AssertError(await ControllerFor(db, "dare-admin").GetTaskResult(Guid.NewGuid().ToString()), 404, FederatedTaskErrorCodes.TaskNotFound);
            AssertError(await ControllerFor(db, "dare-admin").GetTaskResult("not-a-guid"), 404, FederatedTaskErrorCodes.TaskNotFound);
        }

        [Fact]
        public async Task GetTaskResult_failed_task_404_result_not_found()
        {
            using var db = NewDb();
            var seeded = SeedTask(db, 1, TesState.EXECUTORERROREnum);

            var result = await ControllerFor(db, "dare-admin").GetTaskResult(seeded.TaskId.ToString());

            AssertError(result, 404, FederatedTaskErrorCodes.ResultNotFound);
        }

        [Fact]
        public async Task GetTaskResult_storage_down_503()
        {
            using var db = NewDb();
            var (task, store, _) = await CompletedTask(db);
            store.FailGets = true;

            var result = await ControllerFor(db, "dare-admin", store).GetTaskResult(task.TaskId.ToString());

            AssertError(result, 503, FederatedTaskErrorCodes.StorageUnavailable);
        }

        [Fact]
        public async Task GetTaskResult_missing_object_404_result_not_found()
        {
            using var db = NewDb();
            var (task, store, _) = await CompletedTask(db);
            store.Objects.Clear();

            var result = await ControllerFor(db, "dare-admin", store).GetTaskResult(task.TaskId.ToString());

            AssertError(result, 404, FederatedTaskErrorCodes.ResultNotFound);
        }

        [Fact]
        public async Task GetTaskResult_tampered_object_500_and_serves_nothing()
        {
            using var db = NewDb();
            var (task, store, bytes) = await CompletedTask(db);
            store.Objects[task.ResultKey!] = [.. bytes, (byte)' '];

            var result = await ControllerFor(db, "dare-admin", store).GetTaskResult(task.TaskId.ToString());

            AssertError(result, 500, FederatedTaskErrorCodes.InternalError);
            Assert.IsNotType<FileContentResult>(result);
        }

        [Fact]
        public void InvalidModelState_ReturnsErrorBody_NotValidationProblemDetails()
        {
            using var db = NewDb();
            var controller = AgentControllerFor(db, "tre-a-client");
            var actionContext = new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor());
            actionContext.ModelState.AddModelError("State", "Error converting value \"BOGUS\"");
            var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller);

            controller.OnActionExecuting(context);

            var error = AssertError(context.Result!, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Contains("State", error.ErrorMessage);
        }

        [Fact]
        public void ErrorBody_SerializesWithTaskMetadataKeys_AndNoReferenceId()
        {
            // The same settings Submission.Api registers globally.
            var settings = new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects
            };

            var json = JsonConvert.SerializeObject(
                new FederatedApiError { ErrorCode = "task_not_found", ErrorMessage = "task not found" }, settings);

            Assert.Equal("{\"error_code\":\"task_not_found\",\"error_message\":\"task not found\"}", json);
        }
    }
}
