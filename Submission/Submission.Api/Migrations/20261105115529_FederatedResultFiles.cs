using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class FederatedResultFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultRowCount",
                table: "FederatedTasks");

            migrationBuilder.DropColumn(
                name: "ResultValue",
                table: "FederatedTasks");

            migrationBuilder.AddColumn<string>(
                name: "ResultBucket",
                table: "FederatedTasks",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultKey",
                table: "FederatedTasks",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultSha256",
                table: "FederatedTasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultBucket",
                table: "FederatedTasks");

            migrationBuilder.DropColumn(
                name: "ResultKey",
                table: "FederatedTasks");

            migrationBuilder.DropColumn(
                name: "ResultSha256",
                table: "FederatedTasks");

            migrationBuilder.AddColumn<int>(
                name: "ResultRowCount",
                table: "FederatedTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ResultValue",
                table: "FederatedTasks",
                type: "numeric",
                nullable: true);
        }
    }
}
