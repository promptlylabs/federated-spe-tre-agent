using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedResultFilesMigrationTests
    {
        [Fact]
        public void Migration_adds_result_location_columns_and_drops_value_columns()
        {
            var ops = new FederatedResultFiles().UpOperations;

            var added = ops.OfType<AddColumnOperation>().Where(o => o.Table == "FederatedTasks").Select(o => o.Name).ToList();
            var dropped = ops.OfType<DropColumnOperation>().Where(o => o.Table == "FederatedTasks").Select(o => o.Name).ToList();

            Assert.Contains("ResultBucket", added);
            Assert.Contains("ResultKey", added);
            Assert.Contains("ResultSha256", added);
            Assert.Contains("ResultValue", dropped);
            Assert.Contains("ResultRowCount", dropped);
        }

        [Fact]
        public void ToTesTask_complete_returns_location_not_value()
        {
            var sha = new string('a', 64);
            var task = new FederatedTask
            {
                TaskId = Guid.NewGuid(),
                State = TesState.COMPLETEEnum,
                SubmittedAt = DateTime.UtcNow,
                TesTaskJson = "{}",
                ResultBucket = "federated-results",
                ResultKey = $"1/{Guid.NewGuid()}/{sha}.json",
                ResultSha256 = sha,
                DatasetSha256 = new string('b', 64)
            };

            var metadata = task.ToTesTask().Logs![0].Metadata;

            Assert.Equal(
                new[] { "dataset_sha256", "result_bucket", "result_key", "result_sha256" },
                metadata.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
    }
}
