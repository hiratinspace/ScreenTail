using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScreenTail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyTenantMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ST-098: the pilot's two numbers per tenant per ISO week. "Minutes saved" is the pilot's working
            // assumption of five minutes of note-writing avoided per published session until ST-110
            // measures the baseline; the assumption is in one place so it can be replaced with the measured
            // figure. Postgres SQL against EF's PascalCase column names; the tests' SQLite never runs migrations.
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW weekly_tenant_metrics AS
                SELECT "TenantId"                                          AS tenant_id,
                       date_trunc('week', "StartedAt")                     AS week,
                       count(*)                                            AS sessions,
                       count(*) FILTER (WHERE "Published")                 AS published_sessions,
                       avg("EditRatio") FILTER (WHERE "Published")         AS edit_rate,
                       sum("DurationMs") / 60000.0                         AS session_minutes,
                       count(*) FILTER (WHERE "Published") * 5.0           AS minutes_saved
                FROM session_metrics
                GROUP BY "TenantId", date_trunc('week', "StartedAt");
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS weekly_tenant_metrics;");

        }
    }
}
