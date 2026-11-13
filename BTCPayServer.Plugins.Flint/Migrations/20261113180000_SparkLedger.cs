using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class SparkLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SparkLedger",
                schema: "BTCPayServer.Plugins.Flint",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: false),
                    FeeMsat = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SdkPaymentId = table.Column<string>(type: "text", nullable: true),
                    PaymentHash = table.Column<string>(type: "text", nullable: true),
                    BtcPayInvoiceId = table.Column<string>(type: "text", nullable: true),
                    StablecoinQuoteId = table.Column<string>(type: "text", nullable: true),
                    StablecoinChain = table.Column<string>(type: "text", nullable: true),
                    StablecoinAsset = table.Column<string>(type: "text", nullable: true),
                    StablecoinPaidBaseUnits = table.Column<string>(type: "text", nullable: true),
                    StablecoinExternalTxHash = table.Column<string>(type: "text", nullable: true),
                    InvoiceRateEur = table.Column<decimal>(
                        type: "numeric(38,10)", precision: 38, scale: 10, nullable: true),
                    InvoiceRateRon = table.Column<decimal>(
                        type: "numeric(38,10)", precision: 38, scale: 10, nullable: true),
                    InvoiceRateSource = table.Column<string>(type: "text", nullable: true),
                    InvoiceRateAt = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone", nullable: true),
                    IsBackfill = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SparkLedger", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SparkLedger_StoreId_OccurredAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SparkLedger",
                columns: new[] { "StoreId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SparkLedger_StoreId_PaymentHash",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SparkLedger",
                columns: new[] { "StoreId", "PaymentHash" },
                filter: "\"PaymentHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SparkLedger_StoreId_SdkPaymentId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SparkLedger",
                columns: new[] { "StoreId", "SdkPaymentId" },
                filter: "\"SdkPaymentId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SparkLedger",
                schema: "BTCPayServer.Plugins.Flint");
        }
    }
}
