using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TeamTrack.Migrations
{
    /// <inheritdoc />
    public partial class RestructureProductHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Make products.project_id nullable
            migrationBuilder.AlterColumn<int>(
                name: "project_id",
                table: "products",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // 2. Make work_items.project_id nullable
            migrationBuilder.AlterColumn<int>(
                name: "project_id",
                table: "work_items",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // 3. Add product_id to clients
            migrationBuilder.AddColumn<int>(
                name: "product_id",
                table: "clients",
                type: "integer",
                nullable: true);

            // 4. Create client_modules table
            migrationBuilder.CreateTable(
                name: "client_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    client_module_number = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    client_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_modules_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_modules_client_id",
                table: "client_modules",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_client_modules_client_module_number",
                table: "client_modules",
                column: "client_module_number",
                unique: true);

            // 5. Add client_module_id to work_items
            migrationBuilder.AddColumn<int>(
                name: "client_module_id",
                table: "work_items",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_clients_product_id",
                table: "clients",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_items_client_module_id",
                table: "work_items",
                column: "client_module_id");

            migrationBuilder.AddForeignKey(
                name: "FK_clients_products_product_id",
                table: "clients",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_work_items_client_modules_client_module_id",
                table: "work_items",
                column: "client_module_id",
                principalTable: "client_modules",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // 6. Safe data remapping
            migrationBuilder.Sql(@"
                -- Map clients.product_id from existing projects
                UPDATE clients c
                SET product_id = (
                    SELECT prd.id
                    FROM products prd
                    JOIN projects p ON prd.project_id = p.id
                    WHERE p.client_id = c.id
                    LIMIT 1
                )
                WHERE c.product_id IS NULL;

                -- Fallback: If any client has no product yet, assign to first available product
                UPDATE clients c
                SET product_id = (SELECT id FROM products ORDER BY id LIMIT 1)
                WHERE c.product_id IS NULL AND EXISTS (SELECT 1 FROM products);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_work_items_client_modules_client_module_id",
                table: "work_items");

            migrationBuilder.DropForeignKey(
                name: "FK_clients_products_product_id",
                table: "clients");

            migrationBuilder.DropTable(
                name: "client_modules");

            migrationBuilder.DropIndex(
                name: "IX_work_items_client_module_id",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "IX_clients_product_id",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "client_module_id",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "clients");

            migrationBuilder.AlterColumn<int>(
                name: "project_id",
                table: "work_items",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "project_id",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
