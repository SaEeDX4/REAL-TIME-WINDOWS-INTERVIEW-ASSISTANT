using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewAssistant.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rc3RelayIdentityDeletionAndRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityDeletedUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdentityDeletionAttempts",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityDeletionPendingSinceUtc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrialForfeited",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessRevokedUtc",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AllowanceAtHeartbeatSeconds",
                table: "interview_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RelayConnectionId",
                table: "interview_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "billing_adjustments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderSubscriptionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderCustomerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_adjustments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "billing_transactions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderSubscriptionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Origin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Paid = table.Column<bool>(type: "boolean", nullable: false),
                    BilledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PeriodEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_transactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "deleted_account_markers",
                columns: table => new
                {
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deleted_account_markers", x => x.EmailHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_adjustments_ProviderSubscriptionId",
                table: "billing_adjustments",
                column: "ProviderSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_billing_adjustments_TransactionId",
                table: "billing_adjustments",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_billing_transactions_ProviderSubscriptionId",
                table: "billing_transactions",
                column: "ProviderSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_deleted_account_markers_DeletedUtc",
                table: "deleted_account_markers",
                column: "DeletedUtc");

            // Accounts deleted before this release still have their sign-in identity: queue them for deletion.
            migrationBuilder.Sql("UPDATE users SET \"IdentityDeletionPendingSinceUtc\" = \"UpdatedUtc\" WHERE \"Status\" = 'deleted' AND \"IdentityDeletedUtc\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_adjustments");

            migrationBuilder.DropTable(
                name: "billing_transactions");

            migrationBuilder.DropTable(
                name: "deleted_account_markers");

            migrationBuilder.DropColumn(
                name: "IdentityDeletedUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IdentityDeletionAttempts",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IdentityDeletionPendingSinceUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "TrialForfeited",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccessRevokedUtc",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "AllowanceAtHeartbeatSeconds",
                table: "interview_sessions");

            migrationBuilder.DropColumn(
                name: "RelayConnectionId",
                table: "interview_sessions");
        }
    }
}
