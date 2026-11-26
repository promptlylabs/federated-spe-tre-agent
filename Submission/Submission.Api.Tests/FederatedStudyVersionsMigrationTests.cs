using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedStudyVersionsMigrationTests
    {
        [Fact]
        public void Migration_down_keeps_admin_cancels()
        {
            var ops = new AddFederatedStudyOutcome().DownOperations.ToList();

            // Before the outcome columns go, a cancelled version's time moves back to CanceledAt, or the old code would read
            // a version still cleaning up as open and block its study.
            var addCanceledAt = ops.FindIndex(o => o is AddColumnOperation { Name: "CanceledAt" });
            var copy = ops.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains("\"CanceledAt\" = \"OutcomeAt\"") && sql.Sql.Contains("'Canceled'"));
            var dropOutcome = ops.FindIndex(o => o is DropColumnOperation { Name: "Outcome" });
            var dropOutcomeAt = ops.FindIndex(o => o is DropColumnOperation { Name: "OutcomeAt" });
            Assert.True(addCanceledAt >= 0 && addCanceledAt < copy && copy < dropOutcome && copy < dropOutcomeAt);
        }
    }
}
