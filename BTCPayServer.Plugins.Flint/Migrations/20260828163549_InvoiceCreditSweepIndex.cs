using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceCreditSweepIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InvoiceRecords_StoreId_SettledAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                columns: new[] { "StoreId", "SettledAt" },
                filter: "\"Status\" = 1 AND \"CreditedAt\" IS NULL AND \"CreditAbandonedAt\" IS NULL AND \"SettledAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoiceRecords_StoreId_SettledAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords");
        }
    }
}
