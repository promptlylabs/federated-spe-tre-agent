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
using Microsoft.Extensions.Configuration;
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

        // A context with a materialization interceptor gets its own EF service provider, and so its own in-memory
        // store, unless every context names the same root.
        private static ApplicationDbContext OpenDb(string name, Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot root) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name, root).Options);

        private static Tre NewTre(int id, string name, string admin, string clientId) => new()
        {
            Id = id,
            Name = name,
            AdminUsername = admin,
            KeycloakClientId = clientId,
            About = "",
            FormData = ""
        };

        private static FederatedTaskController ControllerFor(ApplicationDbContext db, string username, FakeResultStore? store = null,
            FederatedTaskSettings? settings = null)
        {
            var identity = new ClaimsIdentity([new Claim("preferred_username", username)], "test");
            return new FederatedTaskController(db, store ?? new FakeResultStore(), settings ?? new FederatedTaskSettings())
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
            new(db, store ?? new FakeResultStore(), new FederatedTaskSettings())
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
                Phase = state switch
                {
                    TesState.COMPLETEEnum => FederatedTaskPhase.Released,
                    TesState.EXECUTORERROREnum or TesState.SYSTEMERROREnum => FederatedTaskPhase.Failed,
                    _ => FederatedTaskPhase.Queued
                },
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

        private const string Code = "def site_analysis(df):\n    return {'n': len(df)}\n";

        private static TesTask CodeTask(double? ramGb = 8, long? cpuCores = 2, double? diskGb = 10, string code = Code) => new()
        {
            Executors =
            [
                new TesExecutor
                {
                    Image = "python-runner:1",
                    Command = ["run-analysis", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"]
                }
            ],
            Inputs =
            [
                new TesInput { Path = "/code/analysis.py", Type = TesFileType.FILEEnum, Content = code },
                new TesInput { Url = "dataset://cohort_2024", Path = "/data/cohort_2024", Type = TesFileType.DIRECTORYEnum }
            ],
            Outputs = [new TesOutput { Path = "/outputs/result.json", Url = "result://", Type = TesFileType.FILEEnum }],
            Resources = new TesResources { CpuCores = cpuCores, RamGb = ramGb, DiskGb = diskGb },
            Tags = new Dictionary<string, string>
            {
                ["fed.sites"] = "tre-a",
                ["fed.study_id"] = "study-1",
                ["fed.code_sha256"] = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)))
            }
        };

        [Fact]
        public async Task Enqueue_valid_code_task_is_stored_queued()
        {
            using var db = NewDb();
            var result = await ControllerFor(db, "dare-admin").Enqueue(CodeTask());

            Assert.IsType<OkObjectResult>(result);
            var row = db.FederatedTasks.Single();
            Assert.Equal("run-analysis", row.Operation);
            Assert.Equal("cohort_2024", row.Dataset);
            Assert.Equal(FederatedTaskPhase.Queued, row.Phase);
            Assert.Contains("def site_analysis(df)", row.TesTaskJson);
        }

        // Non-ASCII, a tab and Windows line endings: the JSON serialisation round trip must not change a byte. (The
        // in-memory database keeps the string as is; the Postgres round trip was checked on the dev stack.)
        private const string AwkwardCode = "# é\tcafé\r\ndef site_analysis(df):\r\n    return {'n': len(df)}\r\n";

        [Fact]
        public async Task Agent_of_the_addressed_tre_receives_the_code_unchanged()
        {
            using var db = NewDb();
            Assert.IsType<OkObjectResult>(await ControllerFor(db, "dare-admin").Enqueue(CodeTask(code: AwkwardCode)));

            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var task = Assert.Single(Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value));
            var code = Assert.Single(task.Inputs!, i => i.Path == "/code/analysis.py").Content;
            Assert.Equal(AwkwardCode, code);
            var sha = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code!)));
            Assert.Equal(sha, task.Tags!["fed.code_sha256"]);
        }

        [Fact]
        public async Task Stored_task_cannot_be_changed_after_intake()
        {
            using var db = NewDb();
            Assert.IsType<OkObjectResult>(await ControllerFor(db, "dare-admin").Enqueue(CodeTask()));
            var row = db.FederatedTasks.Single();

            // Init-only stops plain assignments; this is the route EF would still allow.
            db.Entry(row).Property(x => x.TesTaskJson).CurrentValue = "{}";

            var error = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            Assert.Contains("TesTaskJson", error.Message);
        }

        [Fact]
        public async Task Agent_of_another_tre_does_not_receive_the_code()
        {
            using var db = NewDb();
            Assert.IsType<OkObjectResult>(await ControllerFor(db, "dare-admin").Enqueue(CodeTask(code: AwkwardCode)));

            var result = await AgentControllerFor(db, "tre-b-client").GetPendingTasksForTre();

            Assert.Empty(Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value));
        }

        [Fact]
        public async Task Enqueue_code_task_with_unapproved_image_is_rejected_and_not_stored()
        {
            using var db = NewDb();
            var settings = new FederatedTaskSettings { ApprovedRunnerImages = ["python-runner:2"] };

            var result = await ControllerFor(db, "dare-admin", settings: settings).Enqueue(CodeTask());

            AssertError(result, StatusCodes.Status400BadRequest, "invalid_task");
            Assert.Empty(db.FederatedTasks);
        }

        [Fact]
        public async Task Enqueue_code_task_over_resource_limits_is_rejected_and_not_stored()
        {
            using var db = NewDb();

            var result = await ControllerFor(db, "dare-admin").Enqueue(CodeTask(ramGb: 32));

            var error = AssertError(result, StatusCodes.Status400BadRequest, "invalid_task");
            Assert.Contains("resources.ram_gb", error.ErrorMessage);
            Assert.Empty(db.FederatedTasks);
        }

        [Theory]
        [InlineData(0L, 8.0, 10.0)]
        [InlineData(2L, -1.0, 10.0)]
        [InlineData(2L, 8.0, double.NaN)]
        public async Task Non_positive_or_nan_resources_are_rejected(long cpu, double ram, double disk)
        {
            using var db = NewDb();

            var result = await ControllerFor(db, "dare-admin").Enqueue(CodeTask(ram, cpu, disk));

            AssertError(result, StatusCodes.Status400BadRequest, "invalid_task");
            Assert.Empty(db.FederatedTasks);
        }

        [Fact]
        public void Settings_defaults_match_the_spec()
        {
            var empty = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

            foreach (var settings in new[] { new FederatedTaskSettings(), FederatedTaskSettings.FromConfiguration(empty) })
            {
                Assert.Equal(["python-runner:1"], settings.ApprovedRunnerImages);
                Assert.Equal(4, settings.MaxCpuCores);
                Assert.Equal(16, settings.MaxRamGb);
                Assert.Equal(20, settings.MaxDiskGb);
            }
        }

        // The shipped appsettings.json, read from the source tree, layered with an override file as in a deployment.
        private static FederatedTaskSettings SettingsWithOverride(string overrideJson)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Submission", "Submission.Api", "appsettings.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            var config = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(dir!.FullName, "Submission", "Submission.Api", "appsettings.json"))
                .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(overrideJson)))
                .Build();
            return FederatedTaskSettings.FromConfiguration(config);
        }

        [Fact]
        public void An_override_with_an_empty_list_refuses_every_runner_image()
        {
            var settings = SettingsWithOverride("{\"FederatedTaskSettings\":{\"ApprovedRunnerImages\":[]}}");

            Assert.Empty(settings.ApprovedRunnerImages);
        }

        [Fact]
        public void An_override_list_replaces_the_shipped_one_whatever_its_length()
        {
            var settings = SettingsWithOverride("{\"FederatedTaskSettings\":{\"ApprovedRunnerImages\":[\"python-runner:2\"]}}");

            Assert.Equal(["python-runner:2"], settings.ApprovedRunnerImages);
        }

        [Fact]
        public void Without_an_override_the_default_runner_image_is_approved()
        {
            var settings = SettingsWithOverride("{}");

            Assert.Equal(["python-runner:1"], settings.ApprovedRunnerImages);
        }

        [Fact]
        public void Default_settings_have_no_problems()
        {
            Assert.Empty(new FederatedTaskSettings().Problems());
        }

        [Theory]
        [InlineData(0L, 16.0, 20.0, "FederatedTaskSettings:MaxCpuCores must be more than 0")]
        [InlineData(4L, -1.0, 20.0, "FederatedTaskSettings:MaxRamGb must be more than 0")]
        [InlineData(4L, 16.0, double.NaN, "FederatedTaskSettings:MaxDiskGb must be more than 0")]
        public void Non_positive_maximums_are_problems(long cpu, double ram, double disk, string problem)
        {
            var settings = new FederatedTaskSettings { MaxCpuCores = cpu, MaxRamGb = ram, MaxDiskGb = disk };

            Assert.Equal([problem], settings.Problems());
        }

        [Fact]
        public void The_operations_image_in_the_runner_list_is_a_problem()
        {
            var settings = new FederatedTaskSettings { ApprovedRunnerImages = ["5s-tes/federated-ops:1"] };

            Assert.Equal(
                ["FederatedTaskSettings:ApprovedRunnerImages must not list 5s-tes/federated-ops:1, the count/mean image"],
                settings.Problems());
        }

        [Fact]
        public void Configured_runner_images_replace_the_default()
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FederatedTaskSettings:ApprovedRunnerImages:0"] = "python-runner:2",
                    ["FederatedTaskSettings:MaxRamGb"] = "32"
                })
                .Build();

            var settings = FederatedTaskSettings.FromConfiguration(config);

            Assert.Equal(["python-runner:2"], settings.ApprovedRunnerImages);
            Assert.Equal(32, settings.MaxRamGb);
            Assert.Equal(4, settings.MaxCpuCores);
        }

        [Fact]
        public async Task Enqueue_records_queued_phase()
        {
            using var db = NewDb();
            await ControllerFor(db, "dare-admin").Enqueue(MeanTask());

            var row = db.FederatedTasks.Include(x => x.PhaseChanges).Single();
            Assert.Equal(FederatedTaskPhase.Queued, row.Phase);
            var change = Assert.Single(row.PhaseChanges);
            Assert.Equal(FederatedTaskPhase.Queued, change.Phase);
            Assert.Equal(row.SubmittedAt, change.At);
        }

        [Fact]
        public async Task GetTask_queued_task_shows_phase_and_queued_at()
        {
            using var db = NewDb();
            await ControllerFor(db, "dare-admin").Enqueue(MeanTask());
            var id = db.FederatedTasks.Single().TaskId;

            var task = await GetTask(db, id);

            Assert.Equal(TesState.QUEUEDEnum, task.State);
            var metadata = Assert.Single(task.Logs).Metadata;
            Assert.Equal("queued", metadata["phase"]);
            Assert.True(metadata.ContainsKey("queued_at"));
        }

        [Theory]
        [InlineData(TesState.CANCELINGEnum, "CANCELING")]
        [InlineData(TesState.PREEMPTEDEnum, "PREEMPTED")]
        public async Task GetTask_returns_a_tes_1_1_state_as_stored(TesState state, string wire)
        {
            using var db = NewDb();
            var seeded = SeedTask(db, 1, state);

            var task = await GetTask(db, seeded.TaskId);

            Assert.Equal(state, task.State);
            Assert.Contains($"\"state\":\"{wire}\"", JsonConvert.SerializeObject(task));
        }

        [Fact]
        public async Task GetTask_backfilled_task_has_no_running_time()
        {
            using var db = NewDb();
            var seeded = SeedTask(db, 1, TesState.COMPLETEEnum);
            var queuedAt = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
            var releasedAt = queuedAt.AddMinutes(5);
            seeded.Phase = FederatedTaskPhase.Released;
            seeded.PhaseChanges.Add(new FederatedTaskPhaseChange { Phase = FederatedTaskPhase.Queued, At = queuedAt });
            seeded.PhaseChanges.Add(new FederatedTaskPhaseChange { Phase = FederatedTaskPhase.Released, At = releasedAt });
            db.SaveChanges();

            var task = await GetTask(db, seeded.TaskId);

            var log = Assert.Single(task.Logs);
            Assert.False(log.Metadata.ContainsKey("running_at"));
            Assert.Null(log.StartTime);
            Assert.Equal(new DateTimeOffset(releasedAt, TimeSpan.Zero), log.EndTime);
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
            multi.Tags!["fed.sites"] = "tre-a|tre-b";
            yield return [multi];

            var unknown = MeanTask();
            unknown.Tags!["fed.sites"] = "tre-zzz";
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
        public async Task Enqueue_with_only_the_old_tags_is_refused_naming_the_missing_tag()
        {
            using var db = NewDb();
            var task = MeanTask();
            task.Tags = new Dictionary<string, string> { ["tres"] = "tre-a", ["study_id"] = "study-1" };

            var result = await ControllerFor(db, "dare-admin").Enqueue(task);

            var error = AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Contains("fed.sites", error.ErrorMessage);
            Assert.Equal(0, db.FederatedTasks.Count());
        }

        [Fact]
        public async Task Enqueue_keeps_tags_outside_the_namespace()
        {
            using var db = NewDb();
            var task = MeanTask();
            task.Tags!["daams.request"] = "r-42";

            await ControllerFor(db, "dare-admin").Enqueue(task);

            var stored = JsonConvert.DeserializeObject<TesTask>(db.FederatedTasks.Single().TesTaskJson)!;
            Assert.Equal("r-42", stored.Tags!["daams.request"]);
        }

        [Fact]
        public async Task Enqueue_TreLookupIsCaseInsensitive()
        {
            using var db = NewDb();
            var task = MeanTask();
            task.Tags!["fed.sites"] = "TRE-A";
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
            var metadata = Assert.Single(returned.Logs!).Metadata;
            Assert.DoesNotContain("value", metadata.Keys);
            Assert.Equal("queued", metadata["phase"]);
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
                DatasetSha256 = fields.GetValueOrDefault(FederatedReportFields.DatasetSha256),
                RejectedAt = fields.GetValueOrDefault(FederatedReportFields.RejectedAt)
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
        public async Task Report_complete_of_code_task_releases_analysis_result_with_dataset_hash()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            seeded.Operation = "run-analysis";
            db.SaveChanges();
            const string datasetSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var json = "{\"schemaVersion\":1,\"taskId\":\"" + seeded.TaskId.ToString("D") + "\",\"tre\":\"tre-a\"," +
                "\"studyId\":\"study-1\",\"operation\":\"run-analysis\",\"dataset\":\"cohort_2024\"," +
                "\"datasetSha256\":\"" + datasetSha + "\",\"completedAt\":\"2026-11-04T10:00:00+00:00\"," +
                "\"result\":{\"kind\":\"analysis\",\"n\":3,\"value\":{\"mean_age\":40.0,\"n\":3}}}";

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client"), seeded, System.Text.Encoding.UTF8.GetBytes(json));

            Assert.IsType<OkObjectResult>(result);
            var row = db.FederatedTasks.AsNoTracking().Single();
            Assert.Equal(FederatedTaskPhase.Released, row.Phase);
            Assert.Equal(datasetSha, row.DatasetSha256);
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

        private static Task<IActionResult> Cancel(ApplicationDbContext db, Guid taskId) =>
            ControllerFor(db, "dare-admin").Cancel(taskId.ToString());

        [Fact]
        public async Task Cancel_queued_task_is_canceled_at_once_and_never_handed_out()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Queued);

            var result = await Cancel(db, seeded.TaskId);

            Assert.Equal("canceled", Assert.IsType<FederatedProgressResponse>(Assert.IsType<OkObjectResult>(result).Value).Phase);
            var row = Reload(db, seeded.TaskId);
            Assert.Equal(FederatedTaskPhase.Canceled, row.Phase);
            Assert.Equal(TesState.CANCELEDEnum, row.State);
            var pending = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();
            Assert.Empty(Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(pending).Value));
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Running)]
        [InlineData(FederatedTaskPhase.WaitingForOutputApproval)]
        public async Task Cancel_picked_up_task_moves_to_canceling_and_is_handed_out_first(FederatedTaskPhase phase)
        {
            using var db = NewDb();
            SeedAtPhase(db, FederatedTaskPhase.Running, submittedAt: DateTime.UtcNow.AddHours(-2));
            var seeded = SeedAtPhase(db, phase, submittedAt: DateTime.UtcNow.AddHours(-1));

            var result = await Cancel(db, seeded.TaskId);

            Assert.Equal("canceling", Assert.IsType<FederatedProgressResponse>(Assert.IsType<OkObjectResult>(result).Value).Phase);
            var row = Reload(db, seeded.TaskId);
            Assert.Equal(FederatedTaskPhase.Canceling, row.Phase);
            Assert.Equal(TesState.CANCELINGEnum, row.State);
            var pending = Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(
                await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre()).Value);
            Assert.Equal(seeded.TaskId.ToString(), pending[0].Id);
            Assert.Equal(FederatedTaskPhase.Canceling, FederatedTaskPhases.PhaseOf(pending[0]));
        }

        [Fact]
        public async Task Cancel_of_a_canceling_task_changes_nothing()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);

            var result = await Cancel(db, seeded.TaskId);

            Assert.IsType<OkObjectResult>(result);
            AssertUnchanged(db, seeded);
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Released)]
        [InlineData(FederatedTaskPhase.Canceled)]
        public async Task Cancel_of_a_finished_task_is_refused(FederatedTaskPhase phase)
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, phase);

            var result = await Cancel(db, seeded.TaskId);

            AssertError(result, 409, FederatedTaskErrorCodes.TaskAlreadyFinished);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task Cancel_of_an_unknown_task_is_not_found()
        {
            using var db = NewDb();

            AssertError(await Cancel(db, Guid.NewGuid()), 404, FederatedTaskErrorCodes.TaskNotFound);
            AssertError(await ControllerFor(db, "dare-admin").Cancel("nope"), 404, FederatedTaskErrorCodes.TaskNotFound);
        }

        [Fact]
        public void Cancel_is_for_orchestrator_admins_only()
        {
            var authorize = typeof(FederatedTaskController).GetMethod(nameof(FederatedTaskController.Cancel))!
                .GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();

            Assert.Equal("dare-control-admin", authorize?.Roles);
        }

        [Fact]
        public async Task Report_complete_on_a_canceling_task_is_refused_and_stores_no_file()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);
            var store = new FakeResultStore();

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", store), seeded);

            AssertError(result, 409, FederatedTaskErrorCodes.TaskCanceling);
            Assert.Empty(store.Objects);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task Report_error_on_a_canceling_task_is_refused()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"),
                Fields(seeded.TaskId, "SYSTEM_ERROR", FederatedTaskErrorCodes.InternalError, "boom"));

            AssertError(result, 409, FederatedTaskErrorCodes.TaskCanceling);
            AssertUnchanged(db, seeded);
        }

        [Theory]
        [InlineData("running")]
        [InlineData("waiting_for_output_approval")]
        public async Task Progress_on_a_canceling_task_is_refused(string phase)
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);

            var result = await Progress(AgentControllerFor(db, "tre-a-client"), seeded.TaskId, phase);

            AssertError(result, 409, FederatedTaskErrorCodes.TaskCanceling);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task Progress_canceled_confirms_the_cancel()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);

            var result = await Progress(AgentControllerFor(db, "tre-a-client"), seeded.TaskId, "canceled");

            Assert.Equal("canceled", Assert.IsType<FederatedProgressResponse>(Assert.IsType<OkObjectResult>(result).Value).Phase);
            var row = Reload(db, seeded.TaskId);
            Assert.Equal(FederatedTaskPhase.Canceled, row.Phase);
            Assert.Equal(TesState.CANCELEDEnum, row.State);
            var task = await GetTask(db, seeded.TaskId);
            Assert.Equal("canceled", Assert.Single(task.Logs!).Metadata["phase"]);
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Queued)]
        [InlineData(FederatedTaskPhase.Running)]
        [InlineData(FederatedTaskPhase.WaitingForOutputApproval)]
        public async Task Progress_canceled_without_a_cancel_is_refused(FederatedTaskPhase phase)
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, phase);

            var result = await Progress(AgentControllerFor(db, "tre-a-client"), seeded.TaskId, "canceled");

            AssertError(result, 409, FederatedTaskErrorCodes.InvalidTransition);
            AssertUnchanged(db, seeded);
        }

        private static FederatedTask SeedAtPhase(ApplicationDbContext db, FederatedTaskPhase phase, int treId = 1, DateTime? submittedAt = null)
        {
            var task = SeedTask(db, treId, TesState.QUEUEDEnum);
            task.SubmittedAt = submittedAt ?? task.SubmittedAt;
            task.MoveTo(FederatedTaskPhase.Queued, task.SubmittedAt);
            if (phase is FederatedTaskPhase.Canceling or FederatedTaskPhase.Canceled)
            {
                task.MoveTo(FederatedTaskPhase.Running, task.SubmittedAt.AddSeconds(1));
                task.MoveTo(FederatedTaskPhase.Canceling, task.SubmittedAt.AddSeconds(2));
                if (phase == FederatedTaskPhase.Canceled) task.MoveTo(FederatedTaskPhase.Canceled, task.SubmittedAt.AddSeconds(3));
            }
            else if (phase != FederatedTaskPhase.Queued)
            {
                task.MoveTo(phase, task.SubmittedAt.AddSeconds(1));
            }
            db.SaveChanges();
            return task;
        }

        private static FederatedTask Reload(ApplicationDbContext db, Guid id) =>
            db.FederatedTasks.AsNoTracking().Include(x => x.PhaseChanges).Single(x => x.TaskId == id);

        private static void AssertUnchanged(ApplicationDbContext db, FederatedTask seeded)
        {
            var row = Reload(db, seeded.TaskId);
            Assert.Equal(seeded.Phase, row.Phase);
            Assert.Equal(seeded.State, row.State);
            Assert.Equal(seeded.PhaseChanges.Count, row.PhaseChanges.Count);
        }

        [Theory]
        [InlineData(FederatedTaskPhase.Running)]
        [InlineData(FederatedTaskPhase.WaitingForOutputApproval)]
        public async Task ReportResult_complete_from_unfinished_phase_is_released(FederatedTaskPhase from)
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, from);

            var result = await ReportComplete(AgentControllerFor(db, "tre-a-client", new FakeResultStore()), seeded);

            Assert.IsType<OkObjectResult>(result);
            var row = Reload(db, seeded.TaskId);
            Assert.Equal(FederatedTaskPhase.Released, row.Phase);
            Assert.Equal(TesState.COMPLETEEnum, row.State);
        }

        [Theory]
        [InlineData("input_checks", FederatedTaskPhase.RejectedByInputChecks, "invalid_filter")]
        [InlineData("output_approval", FederatedTaskPhase.RejectedAtOutputApproval, "output_rejected")]
        public async Task ReportResult_canceled_is_rejected_with_reason(string rejectedAt, FederatedTaskPhase expected, string code)
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Running);
            var fields = Fields(seeded.TaskId, "CANCELED", code, "not allowed");
            fields[FederatedReportFields.RejectedAt] = rejectedAt;

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), fields);

            Assert.IsType<OkObjectResult>(result);
            var row = Reload(db, seeded.TaskId);
            Assert.Equal(expected, row.Phase);
            Assert.Equal(TesState.CANCELEDEnum, row.State);
            Assert.Equal(code, row.ErrorCode);
            Assert.Equal("not allowed", row.ResultErrorMessage);
            var last = row.PhaseChanges.OrderBy(c => c.Id).Last();
            Assert.Equal(expected, last.Phase);
            Assert.Equal(code, last.ErrorCode);
            Assert.Equal("not allowed", last.ErrorMessage);
        }

        [Fact]
        public async Task ReportResult_rejected_at_with_complete_is_400()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Running);
            var fields = Fields(seeded.TaskId, "COMPLETE");
            fields[FederatedReportFields.RejectedAt] = "input_checks";

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client", new FakeResultStore()), fields,
                (FederatedReportFields.ResultFile, "result.json", ValidResult(seeded)));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task ReportResult_rejected_at_with_error_state_is_400()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Running);
            var fields = Fields(seeded.TaskId, "SYSTEM_ERROR", "boom", "x");
            fields[FederatedReportFields.RejectedAt] = "input_checks";

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), fields);

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            AssertUnchanged(db, seeded);
        }

        [Theory]
        [InlineData(null, "invalid_filter", "msg")]
        [InlineData("elsewhere", "invalid_filter", "msg")]
        [InlineData("input_checks", "invalid_filter", null)]
        [InlineData("input_checks", "invalid_filter", "  ")]
        public async Task ReportResult_canceled_with_bad_fields_is_400(string? rejectedAt, string code, string? message)
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Running);
            var fields = Fields(seeded.TaskId, "CANCELED", code, message);
            if (rejectedAt != null) fields[FederatedReportFields.RejectedAt] = rejectedAt;

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), fields);

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task ReportResult_canceled_with_file_is_400()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Running);
            var fields = Fields(seeded.TaskId, "CANCELED", "invalid_filter", "msg");
            fields[FederatedReportFields.RejectedAt] = "input_checks";

            var result = await ReportAs(AgentControllerFor(db, "tre-a-client"), fields,
                (FederatedReportFields.ResultFile, "result.json", new byte[] { 1 }));

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task GetPending_returns_running_and_waiting_tasks()
        {
            using var db = NewDb();
            SeedAtPhase(db, FederatedTaskPhase.Running);
            SeedAtPhase(db, FederatedTaskPhase.WaitingForOutputApproval);
            SeedAtPhase(db, FederatedTaskPhase.Released);

            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var tasks = Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(new[] { TesState.RUNNINGEnum, TesState.RUNNINGEnum }, tasks.Select(t => t.State).ToArray());
            Assert.Equal(new FederatedTaskPhase?[] { FederatedTaskPhase.Running, FederatedTaskPhase.WaitingForOutputApproval },
                tasks.Select(FederatedTaskPhases.PhaseOf).ToArray());
        }

        [Fact]
        public async Task GetPending_loads_each_tasks_history_with_the_tasks()
        {
            // The test provider has no lazy loading, so the phase times are there only if the query includes them.
            var name = Guid.NewGuid().ToString();
            using (var setup = OpenDb(name))
            {
                setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
                setup.SaveChanges();
                SeedAtPhase(setup, FederatedTaskPhase.Running);
            }

            using var db = OpenDb(name);
            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var task = Assert.Single(Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value));
            var metadata = Assert.Single(task.Logs!).Metadata;
            Assert.True(metadata.ContainsKey("queued_at"));
            Assert.True(metadata.ContainsKey("running_at"));
        }

        [Fact]
        public async Task GetPending_waiting_tasks_do_not_crowd_out_queued()
        {
            using var db = NewDb();
            var start = DateTime.UtcNow.AddHours(-2);
            for (var i = 0; i < 50; i++)
            {
                SeedAtPhase(db, FederatedTaskPhase.WaitingForOutputApproval, submittedAt: start.AddSeconds(i));
            }
            var queued = SeedAtPhase(db, FederatedTaskPhase.Queued, submittedAt: start.AddMinutes(10));
            var running = SeedAtPhase(db, FederatedTaskPhase.Running, submittedAt: start.AddMinutes(20));

            var result = await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre();

            var tasks = Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(50, tasks.Count);
            Assert.Equal(running.TaskId.ToString(), tasks[0].Id);
            Assert.Equal(queued.TaskId.ToString(), tasks[1].Id);
        }

        /// <summary>Runs a competing write once, just before the first save of the context it is attached to.</summary>
        private sealed class BeforeFirstSave(Action competingWrite) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
        {
            private bool _done;

            public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
                Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (!_done)
                {
                    _done = true;
                    competingWrite();
                }

                return ValueTask.FromResult(result);
            }
        }

        [Fact]
        public async Task ReportResult_complete_that_loses_to_a_cancel_is_refused_and_its_file_goes_when_the_cancel_is_confirmed()
        {
            var name = Guid.NewGuid().ToString();
            using var setup = OpenDb(name);
            setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            setup.SaveChanges();
            var seeded = SeedAtPhase(setup, FederatedTaskPhase.WaitingForOutputApproval);

            // The cancel is saved after ReportResult has stored the file and before it saves the task.
            var interceptor = new BeforeFirstSave(() =>
            {
                using var other = OpenDb(name);
                var t = other.FederatedTasks.Include(x => x.PhaseChanges).Single();
                t.MoveTo(FederatedTaskPhase.Canceling, DateTime.UtcNow);
                other.SaveChanges();
            });
            using var dbA = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name).AddInterceptors(interceptor).Options);
            var store = new FakeResultStore();

            var result = await ReportComplete(AgentControllerFor(dbA, "tre-a-client", store), seeded);

            AssertError(result, 409, FederatedTaskErrorCodes.TaskCanceling);
            using var check = OpenDb(name);
            Assert.Equal(FederatedTaskPhase.Canceling, Reload(check, seeded.TaskId).Phase);

            var confirmed = await Progress(AgentControllerFor(check, "tre-a-client", store), seeded.TaskId, "canceled");

            Assert.IsType<OkObjectResult>(confirmed);
            Assert.Empty(store.Objects);
        }

        [Fact]
        public async Task Progress_canceled_deletes_every_result_stored_for_the_task_only()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);
            var store = new FakeResultStore();
            store.Objects[$"1/{seeded.TaskId:D}/{new string('a', 64)}.json"] = [1];
            var other = $"1/{Guid.NewGuid():D}/{new string('b', 64)}.json";
            store.Objects[other] = [2];

            var result = await Progress(AgentControllerFor(db, "tre-a-client", store), seeded.TaskId, "canceled");

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal([other], store.Objects.Keys);
        }

        [Fact]
        public async Task Progress_canceled_when_storage_fails_keeps_the_task_canceling()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);
            var store = new FakeResultStore { FailDeletes = true };

            var result = await Progress(AgentControllerFor(db, "tre-a-client", store), seeded.TaskId, "canceled");

            AssertError(result, 503, FederatedTaskErrorCodes.StorageUnavailable);
            AssertUnchanged(db, seeded);
        }

        [Fact]
        public async Task GetPending_hands_out_a_canceling_task_enqueued_under_an_old_tre_name()
        {
            using var db = NewDb();
            var seeded = SeedAtPhase(db, FederatedTaskPhase.Canceling);
            seeded.TreName = "old-name";
            db.SaveChanges();

            var pending = Assert.IsType<List<TesTask>>(Assert.IsType<OkObjectResult>(
                await AgentControllerFor(db, "tre-a-client").GetPendingTasksForTre()).Value);

            Assert.Equal(seeded.TaskId.ToString(), Assert.Single(pending).Id);
        }

        [Fact]
        public async Task ReportResult_complete_retries_on_top_of_a_concurrent_progress_report()
        {
            var name = Guid.NewGuid().ToString();
            using var setup = OpenDb(name);
            setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            setup.SaveChanges();
            var seeded = SeedAtPhase(setup, FederatedTaskPhase.Queued);

            // The competing progress report is saved after ReportResult has loaded the task and before its first save,
            // so that save hits the Phase concurrency token and the retry has to apply on top of 'running'.
            var interceptor = new BeforeFirstSave(() =>
            {
                using var other = OpenDb(name);
                var t = other.FederatedTasks.Include(x => x.PhaseChanges).Single();
                t.MoveTo(FederatedTaskPhase.Running, DateTime.UtcNow);
                other.SaveChanges();
            });
            using var dbA = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name).AddInterceptors(interceptor).Options);

            var result = await ReportComplete(AgentControllerFor(dbA, "tre-a-client", new FakeResultStore()), seeded);

            Assert.IsType<OkObjectResult>(result);
            using var check = OpenDb(name);
            var row = Reload(check, seeded.TaskId);
            Assert.Equal(FederatedTaskPhase.Released, row.Phase);
            Assert.Equal(TesState.COMPLETEEnum, row.State);
            // The in-memory provider has no transactions, so the history row added by the conflicted first save can
            // survive as a duplicate 'released' row; a relational provider rolls the whole save back. Distinct order is
            // asserted, which still proves the retry applied on top of 'running'.
            Assert.Equal(
                new[] { FederatedTaskPhase.Queued, FederatedTaskPhase.Running, FederatedTaskPhase.Released },
                row.PhaseChanges.OrderBy(c => c.Id).Select(c => c.Phase).Distinct().ToArray());
        }

        private static Task<IActionResult> Progress(FederatedTaskController controller, Guid taskId, string? phase) =>
            controller.ReportProgress(new FederatedTaskProgressReport { TaskId = taskId.ToString(), Phase = phase });

        [Fact]
        public async Task ReportProgress_queued_to_running_records_history()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await Progress(AgentControllerFor(db, "tre-a-client"), seeded.TaskId, "running");

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal("running", Assert.IsType<FederatedProgressResponse>(ok.Value).Phase);
            var row = db.FederatedTasks.AsNoTracking().Include(x => x.PhaseChanges).Single();
            Assert.Equal(FederatedTaskPhase.Running, row.Phase);
            Assert.Equal(TesState.RUNNINGEnum, row.State);
            Assert.Contains(row.PhaseChanges, c => c.Phase == FederatedTaskPhase.Running);
        }

        [Fact]
        public async Task ReportProgress_repeat_running_is_ok_and_adds_no_history()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            var controller = AgentControllerFor(db, "tre-a-client");
            await Progress(controller, seeded.TaskId, "running");

            var result = await Progress(controller, seeded.TaskId, "running");

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal("running", Assert.IsType<FederatedProgressResponse>(ok.Value).Phase);
            var row = db.FederatedTasks.AsNoTracking().Include(x => x.PhaseChanges).Single();
            Assert.Single(row.PhaseChanges, c => c.Phase == FederatedTaskPhase.Running);
        }

        [Fact]
        public async Task ReportProgress_waiting_then_running_is_409_invalid_transition()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            var controller = AgentControllerFor(db, "tre-a-client");
            await Progress(controller, seeded.TaskId, "waiting_for_output_approval");

            var result = await Progress(controller, seeded.TaskId, "running");

            AssertError(result, 409, FederatedTaskErrorCodes.InvalidTransition);
            Assert.Equal(FederatedTaskPhase.WaitingForOutputApproval, db.FederatedTasks.AsNoTracking().Single().Phase);
        }

        [Fact]
        public async Task ReportProgress_on_released_is_409_already_reported_with_sha()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);
            var bytes = ValidResult(seeded);
            await ReportComplete(AgentControllerFor(db, "tre-a-client"), seeded, bytes);

            var result = await Progress(AgentControllerFor(db, "tre-a-client"), seeded.TaskId, "running");

            var error = AssertError(result, 409, FederatedTaskErrorCodes.TaskAlreadyReported);
            Assert.Equal(FederatedResultDocumentJson.Sha256Hex(bytes), error.ResultSha256);
            Assert.NotNull(error.ResultKey);
        }

        [Theory]
        [InlineData("released")]
        [InlineData("failed")]
        [InlineData("queued")]
        [InlineData("bogus")]
        [InlineData(null)]
        public async Task ReportProgress_final_phase_name_is_400(string? phase)
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await Progress(AgentControllerFor(db, "tre-a-client"), seeded.TaskId, phase);

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
            Assert.Equal(FederatedTaskPhase.Queued, db.FederatedTasks.AsNoTracking().Single().Phase);
        }

        [Fact]
        public async Task ReportProgress_bad_guid_is_400()
        {
            using var db = NewDb();

            var result = await AgentControllerFor(db, "tre-a-client")
                .ReportProgress(new FederatedTaskProgressReport { TaskId = "nope", Phase = "running" });

            AssertError(result, 400, FederatedTaskErrorCodes.InvalidTask);
        }

        [Fact]
        public async Task ReportProgress_other_tre_is_404()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await Progress(AgentControllerFor(db, "tre-b-client"), seeded.TaskId, "running");

            AssertError(result, 404, FederatedTaskErrorCodes.TaskNotFound);
            Assert.Equal(FederatedTaskPhase.Queued, db.FederatedTasks.AsNoTracking().Single().Phase);
        }

        [Fact]
        public async Task ReportProgress_non_agent_is_403()
        {
            using var db = NewDb();
            var seeded = SeededTask(db);

            var result = await Progress(ControllerFor(db, "dare-admin"), seeded.TaskId, "running");

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task ReportProgress_racing_final_report_is_409_not_500()
        {
            var name = Guid.NewGuid().ToString();
            using var setup = OpenDb(name);
            setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            setup.SaveChanges();
            var seeded = SeedTask(setup, 1, TesState.QUEUEDEnum);

            // Context A has read the task as queued before the final report is saved through context B.
            using var dbA = OpenDb(name);
            using var dbB = OpenDb(name);
            dbA.FederatedTasks.Include(x => x.PhaseChanges).Single();
            var bytes = ValidResult(seeded);
            Assert.IsType<OkObjectResult>(await ReportComplete(AgentControllerFor(dbB, "tre-a-client"), seeded, bytes));

            var result = await Progress(AgentControllerFor(dbA, "tre-a-client"), seeded.TaskId, "running");

            var error = AssertError(result, 409, FederatedTaskErrorCodes.TaskAlreadyReported);
            Assert.Equal(FederatedResultDocumentJson.Sha256Hex(bytes), error.ResultSha256);
            using var check = OpenDb(name);
            var row = check.FederatedTasks.Include(x => x.PhaseChanges).Single();
            Assert.Equal(FederatedTaskPhase.Released, row.Phase);
            Assert.Equal(TesState.COMPLETEEnum, row.State);
            // The in-memory provider has no transactions, so a running history row added in the failed save can
            // survive here; a relational provider rolls the whole save back. Phase and State are the guarantee tested.
        }

        /// <summary>Before every save, flips the task between queued and running so the save always loses the race.</summary>
        private sealed class BeforeEverySave(string dbName) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
        {
            public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
                Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                using var other = OpenDb(dbName);
                var t = other.FederatedTasks.Single();
                t.Phase = t.Phase == FederatedTaskPhase.Queued ? FederatedTaskPhase.Running : FederatedTaskPhase.Queued;
                other.SaveChanges();
                return ValueTask.FromResult(result);
            }
        }

        private static (string Name, FederatedTask Seeded, ApplicationDbContext Racing) AlwaysRacing()
        {
            var name = Guid.NewGuid().ToString();
            using (var setup = OpenDb(name))
            {
                setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
                setup.SaveChanges();
                var seeded = SeedAtPhase(setup, FederatedTaskPhase.Queued);
                var racing = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase(name).AddInterceptors(new BeforeEverySave(name)).Options);
                return (name, seeded, racing);
            }
        }

        [Fact]
        public async Task ReportProgress_that_keeps_losing_the_race_is_409_task_busy()
        {
            var (name, seeded, racing) = AlwaysRacing();
            using var _ = racing;

            var result = await Progress(AgentControllerFor(racing, "tre-a-client"), seeded.TaskId, "waiting_for_output_approval");

            AssertError(result, 409, FederatedTaskErrorCodes.TaskBusy);
            using var check = OpenDb(name);
            Assert.NotEqual(FederatedTaskPhase.WaitingForOutputApproval, Reload(check, seeded.TaskId).Phase);
        }

        [Fact]
        public async Task ReportResult_that_keeps_losing_the_race_is_409_task_busy()
        {
            var (name, seeded, racing) = AlwaysRacing();
            using var _ = racing;

            var result = await ReportComplete(AgentControllerFor(racing, "tre-a-client", new FakeResultStore()), seeded);

            AssertError(result, 409, FederatedTaskErrorCodes.TaskBusy);
            using var check = OpenDb(name);
            var row = Reload(check, seeded.TaskId);
            Assert.False(FederatedTaskPhases.IsFinal(row.Phase));
            Assert.Equal(TesState.QUEUEDEnum, row.State);
        }

        /// <summary>
        /// Before the first save, moves the task to running through another context so the save loses the race, then
        /// makes the next read of a task fail, as if the database went away.
        /// </summary>
        private sealed class LoseRaceThenFailReread(string dbName, Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot root)
            : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor, Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
        {
            private bool _raced;

            public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
                Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (!_raced)
                {
                    _raced = true;
                    using var other = OpenDb(dbName, root);
                    var t = other.FederatedTasks.Include(x => x.PhaseChanges).Single();
                    t.MoveTo(FederatedTaskPhase.Running, DateTime.UtcNow);
                    other.SaveChanges();
                }

                return ValueTask.FromResult(result);
            }

            public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData materializationData, object entity) =>
                _raced && entity is FederatedTask ? throw new InvalidOperationException("database unavailable") : entity;
        }

        [Fact]
        public async Task ReportResult_reread_failure_after_a_race_is_503_and_records_nothing()
        {
            var name = Guid.NewGuid().ToString();
            var root = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
            using var setup = OpenDb(name, root);
            setup.Tres.Add(NewTre(1, "tre-a", "tre-a-admin", "tre-a-client"));
            setup.SaveChanges();
            var seeded = SeedAtPhase(setup, FederatedTaskPhase.Queued);
            using var racing = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name, root).AddInterceptors(new LoseRaceThenFailReread(name, root)).Options);

            var result = await ReportComplete(AgentControllerFor(racing, "tre-a-client", new FakeResultStore()), seeded);

            AssertError(result, 503, FederatedTaskErrorCodes.StorageUnavailable);
            using var check = OpenDb(name, root);
            var row = Reload(check, seeded.TaskId);
            Assert.Equal(FederatedTaskPhase.Running, row.Phase);
            Assert.Equal(TesState.RUNNINGEnum, row.State);
        }
    }
}
