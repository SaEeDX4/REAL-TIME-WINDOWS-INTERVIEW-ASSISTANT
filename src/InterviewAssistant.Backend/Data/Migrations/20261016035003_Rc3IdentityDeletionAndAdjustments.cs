using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewAssistant.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rc3IdentityDeletionAndAdjustments : Migration
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

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessRevokedUtc",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

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

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "subscriptions",
                type: "text",
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

            migrationBuilder.CreateIndex(
                name: "IX_billing_adjustments_ProviderSubscriptionId",
                table: "billing_adjustments",
                column: "ProviderSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_billing_adjustments_TransactionId",
                table: "billing_adjustments",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_adjustments");

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
                name: "AccessRevokedUtc",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "CurrentTransactionBilledUtc",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "CurrentTransactionId",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "subscriptions");
        }
    }
}
