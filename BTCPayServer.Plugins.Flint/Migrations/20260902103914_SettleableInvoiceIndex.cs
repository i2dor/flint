using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class SettleableInvoiceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InvoiceRecords_StoreId_ExpiresAt_Settleable",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                columns: new[] { "StoreId", "ExpiresAt" },
                filter: "\"Status\" <> 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoiceRecords_StoreId_ExpiresAt_Settleable",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords");
        }
    }
}
