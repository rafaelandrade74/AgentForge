using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPullRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PullRequestNumber",
                table: "WorkItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "WorkItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedGitHubToken",
                table: "Projects",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PullRequestBaseBranch",
                table: "Projects",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PullRequestNumber",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ProtectedGitHubToken",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PullRequestBaseBranch",
                table: "Projects");
        }
    }
}
