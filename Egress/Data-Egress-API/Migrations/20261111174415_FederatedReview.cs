using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data_Egress_API.Migrations
{
    /// <inheritdoc />
    public partial class FederatedReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "EgressSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DecisionDelivered",
                table: "EgressSubmissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReason",
                table: "EgressSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "EgressSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultContent",
                table: "EgressSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultSha256",
                table: "EgressSubmissions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Study",
                table: "EgressSubmissions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Code",
                table: "EgressSubmissions");

            migrationBuilder.DropColumn(
                name: "DecisionDelivered",
                table: "EgressSubmissions");

            migrationBuilder.DropColumn(
                name: "DecisionReason",
                table: "EgressSubmissions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "EgressSubmissions");

            migrationBuilder.DropColumn(
                name: "ResultContent",
                table: "EgressSubmissions");

            migrationBuilder.DropColumn(
                name: "ResultSha256",
                table: "EgressSubmissions");

            migrationBuilder.DropColumn(
                name: "Study",
                table: "EgressSubmissions");
        }
    }
}
