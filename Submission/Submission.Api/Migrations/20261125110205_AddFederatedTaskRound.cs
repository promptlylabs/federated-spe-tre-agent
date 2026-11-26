using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedTaskRound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "FederatedTasks",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Round",
                table: "FederatedTasks");
        }
    }
}
