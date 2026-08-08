using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class SweepRecordFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BalanceAtDecisionSats",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ConfirmationSpeed",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DestinationMode",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "FeesIncluded",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "QuotedFeeSats",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Trigger",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SweepRecords_StoreId_Status",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                columns: new[] { "StoreId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SweepRecords_StoreId_Status",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "BalanceAtDecisionSats",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "ConfirmationSpeed",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "DestinationMode",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "FeesIncluded",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "QuotedFeeSats",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "Trigger",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");
        }
    }
}
