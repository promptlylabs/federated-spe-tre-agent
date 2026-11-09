using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class FederatedTaskStepsMigrationTests
    {
        [Fact]
        public void Migration_adds_step_column_and_history_table()
        {
            var ops = new AddFederatedTaskSteps().UpOperations;

            Assert.Contains(ops.OfType<AddColumnOperation>(), o => o.Table == "FederatedTasks" && o.Name == "Step");
            Assert.Contains(ops.OfType<CreateTableOperation>(), o => o.Name == "FederatedTaskStepChanges");
            Assert.NotEmpty(ops.OfType<SqlOperation>());
        }
    }
}
