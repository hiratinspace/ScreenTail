using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScreenTail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminSignInNonces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_sign_in_nonces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_sign_in_nonces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_sign_in_nonces_ExpiresAt",
                table: "admin_sign_in_nonces",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_sign_in_nonces");
        }
    }
}
