using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class OutgoingPaymentRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutgoingPayments",
                schema: "BTCPayServer.Plugins.Flint",
                columns: table => new
                {
                    PaymentHash = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    Bolt11 = table.Column<string>(type: "text", nullable: false),
                    FirstAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutgoingPayments", x => x.PaymentHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingPayments_StoreId_FirstAttemptAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "OutgoingPayments",
                columns: new[] { "StoreId", "FirstAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutgoingPayments",
                schema: "BTCPayServer.Plugins.Flint");
        }
    }
}
