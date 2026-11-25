using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedStudyVersionsMigrationTests
    {
        [Fact]
        public void Migration_deletes_rows_then_adds_outcome()
        {
            var ops = new AddFederatedStudyOutcome().UpOperations.ToList();

            var delete = Assert.IsType<SqlOperation>(ops[0]);
            Assert.True(delete.Sql.IndexOf("FederatedTaskPhaseChanges") < delete.Sql.IndexOf("\"FederatedTasks\"")
                && delete.Sql.IndexOf("\"FederatedTasks\"") < delete.Sql.IndexOf("FederatedStudyVersions"));
            Assert.Contains(ops.Skip(1), o => o is DropColumnOperation { Table: "FederatedStudyVersions", Name: "CanceledAt" });
            Assert.Equal(new[] { "DeadlineAt", "Outcome", "OutcomeAt", "OutcomeMessage", "OutcomeReason" },
                ops.Skip(1).OfType<AddColumnOperation>().Where(o => o.Table == "FederatedStudyVersions").Select(o => o.Name).Order());
        }
    }
}
