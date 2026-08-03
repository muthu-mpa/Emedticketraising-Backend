using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamTrack.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldChangeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FieldName",
                table: "work_item_activity_logs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NewValue",
                table: "work_item_activity_logs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OldValue",
                table: "work_item_activity_logs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FieldName",
                table: "work_item_activity_logs");

            migrationBuilder.DropColumn(
                name: "NewValue",
                table: "work_item_activity_logs");

            migrationBuilder.DropColumn(
                name: "OldValue",
                table: "work_item_activity_logs");
        }
    }
}
