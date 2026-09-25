using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScreenTail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompanyMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PsaCompany = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocCompanyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocCompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_mappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_company_mappings_TenantId_PsaCompany",
                table: "company_mappings",
                columns: new[] { "TenantId", "PsaCompany" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_mappings");
        }
    }
}
