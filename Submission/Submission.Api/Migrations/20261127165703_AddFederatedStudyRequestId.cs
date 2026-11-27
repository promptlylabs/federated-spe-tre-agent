using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedStudyRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederatedStudyVersions_RequestId",
                table: "FederatedStudyVersions",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FederatedStudyVersions_RequestId",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "FederatedStudyVersions");
        }
    }
}
