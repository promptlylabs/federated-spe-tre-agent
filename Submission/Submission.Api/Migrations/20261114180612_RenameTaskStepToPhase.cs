using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <summary>Renames a federated task's "step" to "phase". Everything is renamed in place, so no history is lost.</summary>
    public partial class RenameTaskStepToPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "Step", table: "FederatedTasks", newName: "Phase");
            migrationBuilder.RenameIndex(name: "IX_FederatedTasks_TreId_Step", table: "FederatedTasks", newName: "IX_FederatedTasks_TreId_Phase");

            migrationBuilder.RenameTable(name: "FederatedTaskStepChanges", newName: "FederatedTaskPhaseChanges");
            migrationBuilder.RenameColumn(name: "Step", table: "FederatedTaskPhaseChanges", newName: "Phase");
            migrationBuilder.RenameIndex(name: "IX_FederatedTaskStepChanges_FederatedTaskId", table: "FederatedTaskPhaseChanges",
                newName: "IX_FederatedTaskPhaseChanges_FederatedTaskId");
            migrationBuilder.Sql("ALTER TABLE \"FederatedTaskPhaseChanges\" RENAME CONSTRAINT \"PK_FederatedTaskStepChanges\" TO \"PK_FederatedTaskPhaseChanges\";");
            migrationBuilder.Sql("ALTER TABLE \"FederatedTaskPhaseChanges\" RENAME CONSTRAINT \"FK_FederatedTaskStepChanges_FederatedTasks_FederatedTaskId\" TO \"FK_FederatedTaskPhaseChanges_FederatedTasks_FederatedTaskId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"FederatedTaskPhaseChanges\" RENAME CONSTRAINT \"FK_FederatedTaskPhaseChanges_FederatedTasks_FederatedTaskId\" TO \"FK_FederatedTaskStepChanges_FederatedTasks_FederatedTaskId\";");
            migrationBuilder.Sql("ALTER TABLE \"FederatedTaskPhaseChanges\" RENAME CONSTRAINT \"PK_FederatedTaskPhaseChanges\" TO \"PK_FederatedTaskStepChanges\";");
            migrationBuilder.RenameIndex(name: "IX_FederatedTaskPhaseChanges_FederatedTaskId", table: "FederatedTaskPhaseChanges",
                newName: "IX_FederatedTaskStepChanges_FederatedTaskId");
            migrationBuilder.RenameColumn(name: "Phase", table: "FederatedTaskPhaseChanges", newName: "Step");
            migrationBuilder.RenameTable(name: "FederatedTaskPhaseChanges", newName: "FederatedTaskStepChanges");

            migrationBuilder.RenameIndex(name: "IX_FederatedTasks_TreId_Phase", table: "FederatedTasks", newName: "IX_FederatedTasks_TreId_Step");
            migrationBuilder.RenameColumn(name: "Phase", table: "FederatedTasks", newName: "Step");
        }
    }
}
