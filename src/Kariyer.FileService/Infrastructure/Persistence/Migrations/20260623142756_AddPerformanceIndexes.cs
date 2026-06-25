using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kariyer.FileService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_CreatedAt",
                schema: "storage",
                table: "StoredFiles",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_UserId_CreatedAt",
                schema: "storage",
                table: "StoredFiles",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_CreatedAt",
                schema: "storage",
                table: "StoredFiles");

            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_UserId_CreatedAt",
                schema: "storage",
                table: "StoredFiles");
        }
    }
}
