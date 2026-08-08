using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.Flint");

            migrationBuilder.CreateTable(
                name: "InvoiceRecords",
                schema: "BTCPayServer.Plugins.Flint",
                columns: table => new
                {
                    PaymentHash = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    Bolt11 = table.Column<string>(type: "text", nullable: false),
                    AmountMsat = table.Column<long>(type: "bigint", nullable: true),
                    AmountReceivedMsat = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Preimage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceRecords", x => x.PaymentHash);
                });

            migrationBuilder.CreateTable(
                name: "SweepRecords",
                schema: "BTCPayServer.Plugins.Flint",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    DestinationAddress = table.Column<string>(type: "text", nullable: false),
                    AmountSats = table.Column<long>(type: "bigint", nullable: false),
                    FeeSats = table.Column<long>(type: "bigint", nullable: true),
                    TxId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweepRecords", x => x.IdempotencyKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceRecords_StoreId_Status",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                columns: new[] { "StoreId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SweepRecords_StoreId_CreatedAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                columns: new[] { "StoreId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceRecords",
                schema: "BTCPayServer.Plugins.Flint");

            migrationBuilder.DropTable(
                name: "SweepRecords",
                schema: "BTCPayServer.Plugins.Flint");
        }
    }
}
