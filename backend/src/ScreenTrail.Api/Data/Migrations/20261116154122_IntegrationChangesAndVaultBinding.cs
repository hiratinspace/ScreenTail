using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScreenTrail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationChangesAndVaultBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BindingVersion",
                table: "integrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "integration_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    PsaCompany = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocCompanyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DocCompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Removed = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "vault_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LegacyRowsClearedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vault_state", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integration_changes_TenantId_At",
                table: "integration_changes",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_changes_one_pruned_row",
                table: "integration_changes",
                column: "TenantId",
                unique: true,
                filter: "\"Action\" = 'mappings_pruned'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_changes");

            migrationBuilder.DropTable(
                name: "vault_state");

            migrationBuilder.DropColumn(
                name: "BindingVersion",
                table: "integrations");
        }
    }
}
