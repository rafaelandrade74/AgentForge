using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PushError",
                table: "WorkItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushedAt",
                table: "WorkItems",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PushError",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "PushedAt",
                table: "WorkItems");
        }
    }
}
