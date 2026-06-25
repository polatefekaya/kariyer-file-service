using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kariyer.FileService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadIdToStoredFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UploadId",
                schema: "storage",
                table: "StoredFiles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadId",
                schema: "storage",
                table: "StoredFiles");
        }
    }
}
