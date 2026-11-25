using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkingHoursAndPracticeInquiries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WeeklyHours",
                table: "reminder_policies",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPractice",
                table: "cases",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // SAWO office hours on the default policy, which is the calendar every reminder and escalation counts with.
            // Friday closes at 16:00. A time zone still on UTC was never set; one an administrator chose is kept.
            migrationBuilder.Sql("""
                UPDATE reminder_policies
                SET "WeeklyHours" = 'Mon-Thu 07:00-18:00, Fri 07:00-16:00',
                    "RestrictToBusinessHours" = TRUE,
                    "BusinessHoursStart" = INTERVAL '07:00',
                    "BusinessHoursEnd" = INTERVAL '18:00',
                    "ExcludeWeekends" = TRUE,
                    "TimeZoneId" = CASE WHEN "TimeZoneId" = 'UTC' THEN 'Asia/Manila' ELSE "TimeZoneId" END
                WHERE "IsDefault" = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WeeklyHours",
                table: "reminder_policies");

            migrationBuilder.DropColumn(
                name: "IsPractice",
                table: "cases");
        }
    }
}
