using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Submission.Api.Controllers;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services;
using Xunit;

namespace Submission.Api.Tests
{
    /// <summary>
    /// Sends real multipart requests over a socket to a minimal Kestrel host that runs the real MVC pipeline
    /// (model binding, the size attributes) with the same <c>MaxRequestBodySize</c> as Program.cs. Program.cs itself
    /// needs Vault and Keycloak, so it is not run. The controller unit tests build the form by hand, so this is also
    /// the only test of real [FromForm] binding and of the form field names.
    /// </summary>
    public class FederatedReportPipelineTests
    {
        private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var identity = new ClaimsIdentity(
                [
                    new Claim("azp", "tre-a-client"),
                    new Claim("preferred_username", "service-account-tre-a-client"),
                    new Claim(ClaimTypes.Role, "dare-tre-admin")
                ], "Test");
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }

        private sealed class Host : IAsyncDisposable
        {
            public required WebApplication App { get; init; }
            public required HttpClient Client { get; init; }
            public required FakeResultStore Store { get; init; }
            public required Guid TaskId { get; init; }

            public FederatedTask Row()
            {
                using var scope = App.Services.CreateScope();
                return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .FederatedTasks.AsNoTracking().Single();
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await App.DisposeAsync();
            }
        }

        private static async Task<Host> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(FederatedReportPipelineTests).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost
                .UseKestrel(o => o.Limits.MaxRequestBodySize = int.MaxValue) // as in Program.cs
                .UseUrls("http://127.0.0.1:0");

            var store = new FakeResultStore();
            var dbName = Guid.NewGuid().ToString();
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<IFederatedResultStore>(store);
            builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
            builder.Services.AddAuthorization();
            builder.Services.AddControllers()
                .AddNewtonsoftJson()
                .AddApplicationPart(typeof(FederatedTaskController).Assembly);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();

            var taskId = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Tres.Add(new Tre { Id = 1, Name = "tre-a", AdminUsername = "tre-a-admin", KeycloakClientId = "tre-a-client", About = "", FormData = "" });
                db.FederatedTasks.Add(new FederatedTask
                {
                    TaskId = taskId,
                    TreId = 1,
                    TreName = "tre-a",
                    StudyId = "study-1",
                    Operation = "mean",
                    Dataset = "cohort_2024",
                    TesTaskJson = JsonConvert.SerializeObject(FederatedTesProfile.Build(
                        new FederatedTaskSpec("mean", "cohort_2024", "age", [], "study-1", "tre-a"))),
                    State = TesState.QUEUEDEnum,
                    SubmittedAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }

            await app.StartAsync();
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Host
            {
                App = app,
                Client = new HttpClient { BaseAddress = new Uri(address) },
                Store = store,
                TaskId = taskId
            };
        }

        private static MultipartFormDataContent Report(Guid taskId, byte[] result)
        {
            var content = new MultipartFormDataContent
            {
                { new StringContent(taskId.ToString()), FederatedReportFields.TaskId },
                { new StringContent("COMPLETE"), FederatedReportFields.State }
            };
            var file = new ByteArrayContent(result);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Add(file, FederatedReportFields.ResultFile, "result.json");
            return content;
        }

        private static byte[] ValidDocument(Guid taskId) => FederatedResultDocumentJson.Serialize(new FederatedResultDocument
        {
            TaskId = taskId.ToString(),
            Tre = "tre-a",
            StudyId = "study-1",
            Operation = "mean",
            Dataset = "cohort_2024",
            DatasetSha256 = new string('a', 64),
            CompletedAt = DateTimeOffset.UtcNow,
            Result = new FederatedScalarResult { Value = 42.5m, RowCount = 10 }
        });

        [Fact]
        public async Task Oversized_report_is_rejected_before_any_write()
        {
            await using var host = await StartAsync();

            using var response = await host.Client.PostAsync("api/FederatedTask/ReportResult",
                Report(host.TaskId, new byte[200_000]));

            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
                $"expected 400 or 413 but got {(int)response.StatusCode}");
            Assert.Empty(host.Store.Objects);
            Assert.Equal(TesState.QUEUEDEnum, host.Row().State);
        }

        [Fact]
        public async Task Valid_report_passes_the_pipeline()
        {
            await using var host = await StartAsync();
            var bytes = ValidDocument(host.TaskId);

            using var response = await host.Client.PostAsync("api/FederatedTask/ReportResult", Report(host.TaskId, bytes));

            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var stored = Assert.Single(host.Store.Objects);
            var sha = FederatedResultDocumentJson.Sha256Hex(bytes);
            var row = host.Row();
            Assert.Equal(TesState.COMPLETEEnum, row.State);
            Assert.Equal(stored.Key, row.ResultKey);
            Assert.Equal(FederatedResultKeys.For(1, host.TaskId, sha), row.ResultKey);
            Assert.Equal(sha, row.ResultSha256);
            Assert.Equal(new string('a', 64), row.DatasetSha256);
            Assert.Equal(bytes, stored.Value);
        }
    }
}
