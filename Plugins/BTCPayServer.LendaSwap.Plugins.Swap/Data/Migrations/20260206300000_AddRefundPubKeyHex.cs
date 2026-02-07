using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundPubKeyHex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefundPubKeyHex",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                type: "character varying(130)",
                maxLength: 130,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefundPubKeyHex",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");
        }
    }
}
