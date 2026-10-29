using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScreenTail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ST-113: the update fields. A policy written before them gets the client's own defaults, the
            // stable channel and a check a day, not the column type's zero, which would turn checks off.
            migrationBuilder.AddColumn<string>(
                name: "UpdateChannel",
                table: "policies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "stable");

            migrationBuilder.AddColumn<bool>(
                name: "UpdateChannelLocked",
                table: "policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UpdateCheckHours",
                table: "policies",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<bool>(
                name: "UpdateChecksUnderLocalOnly",
                table: "policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateChannel",
                table: "policies");

            migrationBuilder.DropColumn(
                name: "UpdateChannelLocked",
                table: "policies");

            migrationBuilder.DropColumn(
                name: "UpdateCheckHours",
                table: "policies");

            migrationBuilder.DropColumn(
                name: "UpdateChecksUnderLocalOnly",
                table: "policies");
        }
    }
}
