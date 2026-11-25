using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Submission.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFederatedStudyAggregation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AggregationErrorCode",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AggregationStartedAt",
                table: "FederatedStudyVersions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PooledResultBucket",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PooledResultKey",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PooledResultSha256",
                table: "FederatedStudyVersions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadyAt",
                table: "FederatedStudyVersions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AggregationErrorCode",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "AggregationStartedAt",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "PooledResultBucket",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "PooledResultKey",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "PooledResultSha256",
                table: "FederatedStudyVersions");

            migrationBuilder.DropColumn(
                name: "ReadyAt",
                table: "FederatedStudyVersions");
        }
    }
}
