using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Submission.Api.Migrations;
using Xunit;

namespace Submission.Api.Tests
{
    public class RenameTaskStepToPhaseMigrationTests
    {
        [Fact]
        public void Up_renames_in_place_and_keeps_the_history()
        {
            var ops = new RenameTaskStepToPhase().UpOperations;

            Assert.DoesNotContain(ops, o => o is DropTableOperation or DropColumnOperation);
            Assert.Contains(ops.OfType<RenameTableOperation>(), o => o.Name == "FederatedTaskStepChanges" && o.NewName == "FederatedTaskPhaseChanges");
            Assert.Contains(ops.OfType<RenameColumnOperation>(), o => o.Table == "FederatedTasks" && o.Name == "Step" && o.NewName == "Phase");
            Assert.Contains(ops.OfType<RenameColumnOperation>(), o => o.Table == "FederatedTaskPhaseChanges" && o.Name == "Step" && o.NewName == "Phase");
        }

        [Fact]
        public void Down_renames_back_without_dropping()
        {
            var ops = new RenameTaskStepToPhase().DownOperations;

            Assert.DoesNotContain(ops, o => o is DropTableOperation or DropColumnOperation);
            Assert.Contains(ops.OfType<RenameTableOperation>(), o => o.Name == "FederatedTaskPhaseChanges" && o.NewName == "FederatedTaskStepChanges");
        }
    }
}
