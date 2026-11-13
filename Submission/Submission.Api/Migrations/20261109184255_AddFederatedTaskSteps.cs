using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedTaskSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FederatedTasks_TreId_State",
                table: "FederatedTasks");

            migrationBuilder.AddColumn<string>(
                name: "Step",
                table: "FederatedTasks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "FederatedTaskStepChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FederatedTaskId = table.Column<int>(type: "integer", nullable: false),
                    Step = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederatedTaskStepChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FederatedTaskStepChanges_FederatedTasks_FederatedTaskId",
                        column: x => x.FederatedTaskId,
                        principalTable: "FederatedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill. State is stored as the TesState int: 2 QUEUED, 6 COMPLETE, 7 EXECUTOR_ERROR, 8 SYSTEM_ERROR.
            migrationBuilder.Sql("""
                UPDATE "FederatedTasks" SET "Step" = CASE "State"
                    WHEN 6 THEN 'Released'
                    WHEN 7 THEN 'Failed'
                    WHEN 8 THEN 'Failed'
                    ELSE 'Queued' END;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "FederatedTaskStepChanges" ("FederatedTaskId", "Step", "At")
                SELECT "Id", 'Queued', "SubmittedAt" FROM "FederatedTasks";
                """);

            migrationBuilder.Sql("""
                INSERT INTO "FederatedTaskStepChanges" ("FederatedTaskId", "Step", "At", "ErrorCode", "ErrorMessage")
                SELECT "Id", "Step", COALESCE("CompletedAt", "SubmittedAt"), "ErrorCode", "ResultErrorMessage"
                FROM "FederatedTasks" WHERE "Step" <> 'Queued';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FederatedTasks_TreId_Step",
                table: "FederatedTasks",
                columns: new[] { "TreId", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_FederatedTaskStepChanges_FederatedTaskId",
                table: "FederatedTaskStepChanges",
                column: "FederatedTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old code only knows QUEUED, COMPLETE and the error states: RUNNING and PAUSED go back to
            // QUEUED, CANCELED becomes EXECUTOR_ERROR.
            migrationBuilder.Sql("""
                UPDATE "FederatedTasks" SET "State" = 2 WHERE "State" IN (4, 5);
                """);
            migrationBuilder.Sql("""
                UPDATE "FederatedTasks" SET "State" = 7 WHERE "State" = 9;
                """);

            migrationBuilder.DropTable(
                name: "FederatedTaskStepChanges");

            migrationBuilder.DropIndex(
                name: "IX_FederatedTasks_TreId_Step",
                table: "FederatedTasks");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "FederatedTasks");

            migrationBuilder.CreateIndex(
                name: "IX_FederatedTasks_TreId_State",
                table: "FederatedTasks",
                columns: new[] { "TreId", "State" });
        }
    }
}
