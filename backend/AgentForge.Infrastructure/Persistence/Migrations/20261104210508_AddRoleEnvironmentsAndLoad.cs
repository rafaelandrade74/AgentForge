using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleEnvironmentsAndLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WaitingReason",
                table: "WorkItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrency",
                table: "ExecutionEnvironments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "AgentRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectRoleEnvironments",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRoleEnvironments", x => new { x.ProjectId, x.Role });
                    table.ForeignKey(
                        name: "FK_ProjectRoleEnvironments_ExecutionEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "ExecutionEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectRoleEnvironments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRoleEnvironments_EnvironmentId",
                table: "ProjectRoleEnvironments",
                column: "EnvironmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectRoleEnvironments");

            migrationBuilder.DropColumn(
                name: "WaitingReason",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "MaxConcurrency",
                table: "ExecutionEnvironments");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "AgentRuns");
        }
    }
}
