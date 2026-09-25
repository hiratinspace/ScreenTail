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
            // figure. Postgres SQL; the tests' SQLite never runs migrations (EnsureCreated).
            migrationBuilder.Sql("""
                CREATE OR REPLACE VIEW weekly_tenant_metrics AS
                SELECT tenant_id,
                       date_trunc('week', started_at) AS week,
                       count(*)                                   AS sessions,
                       count(*) FILTER (WHERE published)          AS published_sessions,
                       avg(edit_ratio) FILTER (WHERE published)   AS edit_rate,
                       sum(duration_ms) / 60000.0                 AS session_minutes,
                       count(*) FILTER (WHERE published) * 5.0    AS minutes_saved
                FROM session_metrics
                GROUP BY tenant_id, date_trunc('week', started_at);
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS weekly_tenant_metrics;");

        }
    }
}
