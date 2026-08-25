using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceRecordCreditedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreditedAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditedAt",
                schema: "BTCPayServer.Plugins.Flint",
                table: "InvoiceRecords");
        }
    }
}
