using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "WorkItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectGroupId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProjectId",
                table: "Demands",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectGroupId",
                table: "Demands",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId",
                table: "WorkItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ProjectGroupId",
                table: "Projects",
                column: "ProjectGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Demands_ProjectGroupId",
                table: "Demands",
                column: "ProjectGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectGroups_Name",
                table: "ProjectGroups",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Demands_ProjectGroups_ProjectGroupId",
                table: "Demands",
                column: "ProjectGroupId",
                principalTable: "ProjectGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_ProjectGroups_ProjectGroupId",
                table: "Projects",
                column: "ProjectGroupId",
                principalTable: "ProjectGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItems_Projects_ProjectId",
                table: "WorkItems",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Demands_ProjectGroups_ProjectGroupId",
                table: "Demands");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_ProjectGroups_ProjectGroupId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkItems_Projects_ProjectId",
                table: "WorkItems");

            migrationBuilder.DropTable(
                name: "ProjectGroups");

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_Projects_ProjectGroupId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Demands_ProjectGroupId",
                table: "Demands");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ProjectGroupId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ProjectGroupId",
                table: "Demands");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProjectId",
                table: "Demands",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
