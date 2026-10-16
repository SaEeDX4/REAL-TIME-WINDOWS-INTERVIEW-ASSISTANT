using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewAssistant.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rc3SelfReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentTransactionBilledUtc",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "CurrentTransactionId",
                table: "subscriptions");

            migrationBuilder.AddColumn<bool>(
                name: "TrialForfeited",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RelayConnectionId",
                table: "interview_sessions",
                type: "uuid",
                nullable: true);

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
                name: "IX_billing_transactions_ProviderSubscriptionId",
                table: "billing_transactions",
                column: "ProviderSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_deleted_account_markers_DeletedUtc",
                table: "deleted_account_markers",
                column: "DeletedUtc");

            // Accounts deleted before identity deletion existed still have their sign-in identity: queue them.
            migrationBuilder.Sql("UPDATE users SET \"IdentityDeletionPendingSinceUtc\" = \"UpdatedUtc\" WHERE \"Status\" = 'deleted' AND \"IdentityDeletedUtc\" IS NULL AND \"IdentityDeletionPendingSinceUtc\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_transactions");

            migrationBuilder.DropTable(
                name: "deleted_account_markers");

            migrationBuilder.DropColumn(
                name: "TrialForfeited",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RelayConnectionId",
                table: "interview_sessions");

            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentTransactionBilledUtc",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentTransactionId",
                table: "subscriptions",
                type: "text",
                nullable: true);
        }
    }
}
