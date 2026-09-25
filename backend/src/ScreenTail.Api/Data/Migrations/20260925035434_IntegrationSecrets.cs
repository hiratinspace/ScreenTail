using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScreenTail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecretRef",
                table: "integrations");

            migrationBuilder.AddColumn<byte[]>(
                name: "DataKeyNonce",
                table: "integrations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "DataKeyWrapped",
                table: "integrations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "integrations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RotatedAt",
                table: "integrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SecretCiphertext",
                table: "integrations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretHint",
                table: "integrations",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SecretNonce",
                table: "integrations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteUrl",
                table: "integrations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataKeyNonce",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "DataKeyWrapped",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "KeyId",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "RotatedAt",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "SecretCiphertext",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "SecretHint",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "SecretNonce",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "SiteUrl",
                table: "integrations");

            migrationBuilder.AddColumn<string>(
                name: "SecretRef",
                table: "integrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }
    }
}
