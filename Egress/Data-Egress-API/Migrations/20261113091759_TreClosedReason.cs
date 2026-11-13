using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data_Egress_API.Migrations
{
    /// <inheritdoc />
    public partial class TreClosedReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TreClosedReason",
                table: "EgressSubmissions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TreClosedReason",
                table: "EgressSubmissions");
        }
    }
}
