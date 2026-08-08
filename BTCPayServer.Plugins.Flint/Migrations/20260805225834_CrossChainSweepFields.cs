using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class CrossChainSweepFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConversionStatus",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveredAmountBaseUnits",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationAsset",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationAssetDecimals",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DestinationChain",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationKind",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EstimatedOutBaseUnits",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            // true, not the scaffolder's false, and this is the one hand edit in this migration.
            //
            // Every row that exists before this wave is a cooperative exit, and every cooperative exit carries
            // an idempotency key the SDK adopted as its own payment id. Backfilling false would tell the sweep
            // engine's crash-recovery walk that those rows must be resolved by provider quote id instead — a
            // column that is null on all of them — so any sweep that was in flight across the upgrade would be
            // reported as unresolvable and written off after the grace period, for a send that very likely
            // succeeded and can be looked up perfectly well.
            //
            // The property initialiser on SweepRecord is also true, so new rows agree; only a token-leg
            // cross-chain send ever sets it false.
            migrationBuilder.AddColumn<bool>(
                name: "IdempotencyKeyAccepted",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderOrderId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderQuoteId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAmountBaseUnits",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTokenDecimals",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceTokenIdentifier",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversionStatus",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "DeliveredAmountBaseUnits",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "DestinationAsset",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "DestinationAssetDecimals",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "DestinationChain",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "DestinationKind",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "EstimatedOutBaseUnits",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyAccepted",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "ProviderOrderId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "ProviderQuoteId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "SourceAmountBaseUnits",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "SourceTokenDecimals",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "SourceTokenIdentifier",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");
        }
    }
}
