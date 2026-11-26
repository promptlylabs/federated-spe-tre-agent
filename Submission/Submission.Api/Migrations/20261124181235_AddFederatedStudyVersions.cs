using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedStudyVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every task now belongs to a study version, and old tasks have none to point at. Only the dev stack holds
            // federated tasks, so they are deleted rather than backfilled.
            migrationBuilder.Sql("DELETE FROM \"FederatedTaskPhaseChanges\"; DELETE FROM \"FederatedTasks\";");

            migrationBuilder.CreateTable(
                name: "FederatedStudyVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudyId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Sites = table.Column<string>(type: "text", nullable: false),
                    CodeSha256 = table.Column<string>(type: "text", nullable: false),
                    TesTaskJson = table.Column<string>(type: "text", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CanceledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederatedStudyVersions", x => x.Id);
                });

            migrationBuilder.AddColumn<int>(
                name: "StudyVersionId",
                table: "FederatedTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_FederatedTasks_StudyVersionId",
                table: "FederatedTasks",
                column: "StudyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_FederatedStudyVersions_StudyId_Version",
                table: "FederatedStudyVersions",
                columns: new[] { "StudyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederatedStudyVersions_VersionId",
                table: "FederatedStudyVersions",
                column: "VersionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FederatedTasks_FederatedStudyVersions_StudyVersionId",
                table: "FederatedTasks",
                column: "StudyVersionId",
                principalTable: "FederatedStudyVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FederatedTasks_FederatedStudyVersions_StudyVersionId",
                table: "FederatedTasks");

            migrationBuilder.DropTable(
                name: "FederatedStudyVersions");

            migrationBuilder.DropIndex(
                name: "IX_FederatedTasks_StudyVersionId",
                table: "FederatedTasks");

            migrationBuilder.DropColumn(
                name: "StudyVersionId",
                table: "FederatedTasks");
        }
    }
}
