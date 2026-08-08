using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class SweepRefusalRecurrence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                // 1, not 0: every row that already exists represents exactly one attempt, and backfilling zero
                // would make the history claim a sweep never happened.
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RefusalCode",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");

            migrationBuilder.DropColumn(
                name: "RefusalCode",
                schema: "BTCPayServer.Plugins.Flint",
                table: "SweepRecords");
        }
    }
}
