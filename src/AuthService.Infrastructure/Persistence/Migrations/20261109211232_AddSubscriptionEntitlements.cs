using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_application_id",
                table: "subscriptions");

            migrationBuilder.CreateSequence(
                name: "entitlement_change_seq");

            migrationBuilder.AddColumn<long>(
                name: "entitlements_change_seq",
                table: "subscriptions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "entitlements_version",
                table: "subscriptions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "plan_id",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "entitlement_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plan_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    change = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    limit_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    old_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_changes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    limit_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    set_by_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    set_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_overrides", x => x.id);
                    table.CheckConstraint("ck_subscription_overrides_value_not_negative", "value IS NULL OR value >= 0");
                    table.ForeignKey(
                        name: "fk_subscription_overrides_limit_definitions_limit_definition_id",
                        column: x => x.limit_definition_id,
                        principalTable: "limit_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscription_overrides_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_application_id_entitlements_change_seq",
                table: "subscriptions",
                columns: new[] { "application_id", "entitlements_change_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_plan_id",
                table: "subscriptions",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_changes_application_id_occurred_at",
                table: "entitlement_changes",
                columns: new[] { "application_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_changes_tenant_id_occurred_at",
                table: "entitlement_changes",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_overrides_limit_definition_id",
                table: "subscription_overrides",
                column: "limit_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_overrides_subscription_id_limit_definition_id",
                table: "subscription_overrides",
                columns: new[] { "subscription_id", "limit_definition_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_plans_plan_id",
                table: "subscriptions",
                column: "plan_id",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_plans_plan_id",
                table: "subscriptions");

            migrationBuilder.DropTable(
                name: "entitlement_changes");

            migrationBuilder.DropTable(
                name: "subscription_overrides");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_application_id_entitlements_change_seq",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_plan_id",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "entitlements_change_seq",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "entitlements_version",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "subscriptions");

            migrationBuilder.DropSequence(
                name: "entitlement_change_seq");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_application_id",
                table: "subscriptions",
                column: "application_id");
        }
    }
}
