using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedStudyVersionsMigrationTests
    {
        [Fact]
        public void Migration_deletes_tasks_then_adds_versions()
        {
            var ops = new AddFederatedStudyVersions().UpOperations.ToList();

            var delete = Assert.IsType<SqlOperation>(ops[0]);
            Assert.Contains("FederatedTaskPhaseChanges", delete.Sql);
            Assert.Contains("FederatedTasks", delete.Sql);

            var create = ops.IndexOf(ops.OfType<CreateTableOperation>().Single(o => o.Name == "FederatedStudyVersions"));
            var column = ops.IndexOf(ops.OfType<AddColumnOperation>().Single(o => o.Table == "FederatedTasks" && o.Name == "StudyVersionId"));
            var index = ops.IndexOf(ops.OfType<CreateIndexOperation>().Single(o =>
                o.Table == "FederatedStudyVersions" && o.IsUnique && o.Columns.SequenceEqual(new[] { "StudyId", "Version" })));
            Assert.True(0 < create && create < column && column < index);
        }
    }
}
