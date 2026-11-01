using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deadline_at",
                table: "webhook_outbox",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                table: "webhook_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_secret_protected",
                table: "applications",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_url",
                table: "applications",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            // Events queued before this migration get the same 24 hours to be delivered as new ones, counted from their creation.
            migrationBuilder.Sql("UPDATE webhook_outbox SET deadline_at = created_at + interval '24 hours';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deadline_at",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "webhook_secret_protected",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "webhook_url",
                table: "applications");
        }
    }
}
