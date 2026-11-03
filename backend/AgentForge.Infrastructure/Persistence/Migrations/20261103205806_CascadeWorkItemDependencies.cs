using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CascadeWorkItemDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkItemDependencies_WorkItems_DependsOnId",
                table: "WorkItemDependencies");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItemDependencies_WorkItems_DependsOnId",
                table: "WorkItemDependencies",
                column: "DependsOnId",
                principalTable: "WorkItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkItemDependencies_WorkItems_DependsOnId",
                table: "WorkItemDependencies");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkItemDependencies_WorkItems_DependsOnId",
                table: "WorkItemDependencies",
                column: "DependsOnId",
                principalTable: "WorkItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
