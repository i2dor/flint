using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class OutgoingPaymentStoreScopedKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_OutgoingPayments",
                schema: "BTCPayServer.Plugins.Flint",
                table: "OutgoingPayments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OutgoingPayments",
                schema: "BTCPayServer.Plugins.Flint",
                table: "OutgoingPayments",
                columns: new[] { "StoreId", "PaymentHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_OutgoingPayments",
                schema: "BTCPayServer.Plugins.Flint",
                table: "OutgoingPayments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OutgoingPayments",
                schema: "BTCPayServer.Plugins.Flint",
                table: "OutgoingPayments",
                column: "PaymentHash");
        }
    }
}
