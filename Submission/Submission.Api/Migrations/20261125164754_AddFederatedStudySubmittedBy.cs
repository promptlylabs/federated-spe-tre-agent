using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedStudySubmittedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmittedBy",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubmittedBy",
                table: "FederatedStudyVersions");
        }
    }
}
