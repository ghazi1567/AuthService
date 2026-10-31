using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSigningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    public_key_x = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    public_key_y = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    encrypted_private_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    publish_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sign_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sign_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    publish_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signing_keys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_signing_keys_key_id",
                table: "signing_keys",
                column: "key_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signing_keys");
        }
    }
}
