using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceRecordSettlementFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SdkPaymentId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceRecords_StoreId_CreatedAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                columns: new[] { "StoreId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoiceRecords_StoreId_CreatedAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords");

            migrationBuilder.DropColumn(
                name: "SdkPaymentId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords");
        }
    }
}
