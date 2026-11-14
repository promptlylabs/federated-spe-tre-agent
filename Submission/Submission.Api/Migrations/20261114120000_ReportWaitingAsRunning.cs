using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <summary>
    /// A task waiting for output approval is reported as TES RUNNING, no longer PAUSED. State is stored as the TesState
    /// number: 4 RUNNING, 5 PAUSED.
    /// </summary>
    public partial class ReportWaitingAsRunning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"FederatedTasks\" SET \"State\" = 4 WHERE \"Step\" = 'WaitingForOutputApproval' AND \"State\" = 5;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"FederatedTasks\" SET \"State\" = 5 WHERE \"Step\" = 'WaitingForOutputApproval' AND \"State\" = 4;");
        }
    }
}
