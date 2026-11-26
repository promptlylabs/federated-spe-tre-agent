using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedStudyOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only the dev stack has federated rows; versions from before the outcome have no deadline to decide by.
            migrationBuilder.Sql("DELETE FROM \"FederatedTaskPhaseChanges\"; DELETE FROM \"FederatedTasks\"; DELETE FROM \"FederatedStudyVersions\";");

            // An admin cancel is now the canceled outcome.
            migrationBuilder.DropColumn(
                name: "CanceledAt",
                table: "FederatedStudyVersions");

            migrationBuilder.AddColumn<DateTime>(
                name: "OutcomeAt",
                table: "FederatedStudyVersions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeadlineAt",
                table: "FederatedStudyVersions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeMessage",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeReason",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederatedStudyVersions_Outcome",
                table: "FederatedStudyVersions",
                column: "Outcome");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CanceledAt",
                table: "FederatedStudyVersions",
                type: "timestamp with time zone",
                nullable: true);

            // Keep admin cancels: without CanceledAt the old code reads a cancelled version still cleaning up as open.
            migrationBuilder.Sql("UPDATE \"FederatedStudyVersions\" SET \"CanceledAt\" = \"OutcomeAt\" WHERE \"Outcome\" = 'Canceled';");

            migrationBuilder.DropIndex(
                name: "IX_FederatedStudyVersions_Outcome",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "DeadlineAt",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "OutcomeMessage",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "OutcomeReason",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "OutcomeAt",
                table: "FederatedStudyVersions");
        }
    }
}
