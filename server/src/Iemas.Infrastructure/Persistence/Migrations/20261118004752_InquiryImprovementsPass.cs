using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Iemas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InquiryImprovementsPass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EmailMessageId",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDeliveryAttemptAt",
                table: "escalation_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewDecision",
                table: "email_classifications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "email_classifications",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "email_classifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedByEmail",
                table: "email_classifications",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                table: "email_classifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "ai_usage_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "ai_usage_records",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Decision",
                table: "ai_usage_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Explanation",
                table: "ai_usage_records",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCategory",
                table: "ai_usage_records",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FallbackUsed",
                table: "ai_usage_records",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelVersion",
                table: "ai_usage_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "ai_usage_records",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "ai_classification_logs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CostUsd",
                table: "ai_classification_logs",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FallbackUsed",
                table: "ai_classification_logs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelVersion",
                table: "ai_classification_logs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "ai_classification_logs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTokens",
                table: "ai_classification_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_CaseId_EmailMessageId_Type",
                table: "notifications",
                columns: new[] { "CaseId", "EmailMessageId", "Type" },
                unique: true,
                filter: "\"EmailMessageId\" IS NOT NULL");

            // Older supervision runs could record the same executed level twice for a case.
            // Deleting those rows would destroy history, so the unique index is only created
            // when the data is already clean. Otherwise a NOTICE is raised and the application
            // level guard in EscalationService still prevents new duplicates.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM escalation_events
                        WHERE "Outcome" = 0
                        GROUP BY "CaseId", "Level"
                        HAVING COUNT(*) > 1
                    ) THEN
                        CREATE UNIQUE INDEX IF NOT EXISTS "IX_escalation_events_CaseId_Level"
                            ON escalation_events ("CaseId", "Level")
                            WHERE "Outcome" = 0;
                    ELSE
                        RAISE NOTICE 'IX_escalation_events_CaseId_Level not created: duplicate executed escalations exist. Review them, then create the index manually.';
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_CaseId_EmailMessageId_Type",
                table: "notifications");

            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_escalation_events_CaseId_Level\";");

            migrationBuilder.DropColumn(
                name: "EmailMessageId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "LastDeliveryAttemptAt",
                table: "escalation_events");

            migrationBuilder.DropColumn(
                name: "ReviewDecision",
                table: "email_classifications");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "email_classifications");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "email_classifications");

            migrationBuilder.DropColumn(
                name: "ReviewedByEmail",
                table: "email_classifications");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                table: "email_classifications");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "Explanation",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "FailureCategory",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "FallbackUsed",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "ModelVersion",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "PromptVersion",
                table: "ai_usage_records");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "ai_classification_logs");

            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "ai_classification_logs");

            migrationBuilder.DropColumn(
                name: "FallbackUsed",
                table: "ai_classification_logs");

            migrationBuilder.DropColumn(
                name: "ModelVersion",
                table: "ai_classification_logs");

            migrationBuilder.DropColumn(
                name: "PromptVersion",
                table: "ai_classification_logs");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                table: "ai_classification_logs");
        }
    }
}
