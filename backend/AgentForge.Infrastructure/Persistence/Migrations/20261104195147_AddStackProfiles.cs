using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStackProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StackProfileId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StackProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Image = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Dockerfile = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Network = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CpuLimit = table.Column<double>(type: "double precision", nullable: false),
                    MemoryMb = table.Column<int>(type: "integer", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StackProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_StackProfileId",
                table: "Projects",
                column: "StackProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_StackProfiles_Name",
                table: "StackProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_StackProfiles_StackProfileId",
                table: "Projects",
                column: "StackProfileId",
                principalTable: "StackProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_StackProfiles_StackProfileId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "StackProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Projects_StackProfileId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "StackProfileId",
                table: "Projects");
        }
    }
}
