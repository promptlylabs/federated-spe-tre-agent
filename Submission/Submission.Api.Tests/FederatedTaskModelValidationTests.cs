using FiveSafesTes.Core.Models.Tes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;

namespace Submission.Api.Tests
{
    // Runs the same MVC model validation Enqueue's [FromBody] binding runs, which the controller tests bypass.
    public class FederatedTaskModelValidationTests
    {
        // The code-task example from docs/federated-tasks.md: GA4GH-optional fields (name, description) omitted.
        private const string DocExample = """
            {
              "executors": [
                {
                  "image": "python-runner:1",
                  "command": ["run-analysis", "/code/analysis.py", "/data/cohort_2024", "/outputs/result.json"]
                }
              ],
              "inputs": [
                { "path": "/code/analysis.py", "type": "FILE", "content": "def site_analysis(df):\n    return {'n': len(df), 'mean_age': float(df['age'].mean())}\n" },
                { "url": "dataset://cohort_2024", "path": "/data/cohort_2024", "type": "DIRECTORY" }
              ],
              "outputs": [{ "path": "/outputs/result.json", "url": "result://", "type": "FILE" }],
              "resources": { "cpu_cores": 2, "ram_gb": 8 },
              "tags": {
                "fed.sites": "my-tre",
                "fed.study_id": "study-001",
                "fed.code_sha256": "<lowercase hex SHA-256 of the content>"
              }
            }
            """;

        [Fact]
        public void DocExample_PassesModelValidation()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddControllers();
            var provider = services.BuildServiceProvider();
            var validator = provider.GetRequiredService<IObjectModelValidator>();
            var context = new ActionContext(
                new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());

            var task = JsonConvert.DeserializeObject<TesTask>(DocExample)!;
            validator.Validate(context, null, string.Empty, task);

            var errors = context.ModelState
                .Where(e => e.Value!.Errors.Count > 0)
                .Select(e => e.Key);
            Assert.True(context.ModelState.IsValid, "invalid: " + string.Join(", ", errors));
        }
    }
}
