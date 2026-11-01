using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    secret_salt = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.UniqueConstraint("ak_api_keys_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_api_keys_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_api_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_api_keys_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_key_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_key_permissions_api_keys_tenant_id_api_key_id",
                        columns: x => new { x.tenant_id, x.api_key_id },
                        principalTable: "api_keys",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_api_key_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_key_permissions_api_key_id_permission_id",
                table: "api_key_permissions",
                columns: new[] { "api_key_id", "permission_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_key_permissions_permission_id",
                table: "api_key_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_key_permissions_tenant_id_api_key_id",
                table: "api_key_permissions",
                columns: new[] { "tenant_id", "api_key_id" });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_application_id",
                table: "api_keys",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_created_by_user_id",
                table: "api_keys",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key_id",
                table: "api_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_tenant_id",
                table: "api_keys",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key_permissions");

            migrationBuilder.DropTable(
                name: "api_keys");
        }
    }
}
