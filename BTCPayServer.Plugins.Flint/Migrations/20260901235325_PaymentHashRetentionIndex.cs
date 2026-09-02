using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class PaymentHashRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InvoicePaymentHashes_FirstSeenAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoicePaymentHashes",
                column: "FirstSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoicePaymentHashes_FirstSeenAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoicePaymentHashes");
        }
    }
}
