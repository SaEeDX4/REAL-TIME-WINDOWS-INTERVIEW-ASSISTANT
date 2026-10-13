using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewAssistant.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductionHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscriptions_ProviderSubscriptionId",
                table: "subscriptions");

            // Data: retired development plan code → production code (no-op when absent).
            migrationBuilder.Sql("UPDATE subscriptions SET \"PlanCode\" = 'pro' WHERE \"PlanCode\" = 'pro-dev';");
            // Data: keep the most recently updated row per provider subscription id so the unique index below cannot fail.
            migrationBuilder.Sql(@"UPDATE subscriptions s SET ""ProviderSubscriptionId"" = NULL
                WHERE ""ProviderSubscriptionId"" IS NOT NULL AND EXISTS (
                  SELECT 1 FROM subscriptions o WHERE o.""ProviderSubscriptionId"" = s.""ProviderSubscriptionId"" AND o.""Id"" <> s.""Id""
                  AND (o.""UpdatedUtc"" > s.""UpdatedUtc"" OR (o.""UpdatedUtc"" = s.""UpdatedUtc"" AND o.""Id"" > s.""Id"")));");

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "webhook_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "webhook_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderSubscriptionId",
                table: "subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderCustomerId",
                table: "subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "subscriptions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_events_ReceivedUtc",
                table: "webhook_events",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_ProviderSubscriptionId",
                table: "subscriptions",
                column: "ProviderSubscriptionId",
                unique: true,
                filter: "\"ProviderSubscriptionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_events_ReceivedUtc",
                table: "webhook_events");

            migrationBuilder.DropIndex(
                name: "IX_subscriptions_ProviderSubscriptionId",
                table: "subscriptions");

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "webhook_events",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "webhook_events",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderSubscriptionId",
                table: "subscriptions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderCustomerId",
                table: "subscriptions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "subscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_ProviderSubscriptionId",
                table: "subscriptions",
                column: "ProviderSubscriptionId");
        }
    }
}
