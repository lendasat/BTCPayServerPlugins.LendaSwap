using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTxIdField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TxId",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TxId",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");
        }
    }
}
