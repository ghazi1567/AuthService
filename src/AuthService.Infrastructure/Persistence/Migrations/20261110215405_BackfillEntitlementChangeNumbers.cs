using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ENT-05: subscriptions from before ENT-03 still have version 0 and change number 0, so they would never appear in a product's
    /// "what changed since" read (which starts after 0) and would all share one position. Each gets version 1 and its own change number.
    /// No event is sent: nothing changed for the product, which picks them up on its first sync.
    /// </summary>
    public partial class BackfillEntitlementChangeNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE subscriptions
                SET entitlements_version = 1, entitlements_change_seq = nextval('entitlement_change_seq')
                WHERE entitlements_change_seq = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: version 1 and a change number are what every subscription has from now on.
        }
    }
}
