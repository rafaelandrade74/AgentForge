using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemandBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BranchName",
                table: "Demands",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PullRequestNumber",
                table: "Demands",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "Demands",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PushError",
                table: "Demands",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushedAt",
                table: "Demands",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BranchName",
                table: "Demands");

            migrationBuilder.DropColumn(
                name: "PullRequestNumber",
                table: "Demands");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "Demands");

            migrationBuilder.DropColumn(
                name: "PushError",
                table: "Demands");

            migrationBuilder.DropColumn(
                name: "PushedAt",
                table: "Demands");
        }
    }
}
