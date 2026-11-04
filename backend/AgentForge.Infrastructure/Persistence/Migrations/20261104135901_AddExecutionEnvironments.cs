using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExecutionEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProtectedToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    WorkspaceRoot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Tags = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionEnvironments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_EnvironmentId",
                table: "Projects",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionEnvironments_Name",
                table: "ExecutionEnvironments",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_ExecutionEnvironments_EnvironmentId",
                table: "Projects",
                column: "EnvironmentId",
                principalTable: "ExecutionEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_ExecutionEnvironments_EnvironmentId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "ExecutionEnvironments");

            migrationBuilder.DropIndex(
                name: "IX_Projects_EnvironmentId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "Projects");
        }
    }
}
