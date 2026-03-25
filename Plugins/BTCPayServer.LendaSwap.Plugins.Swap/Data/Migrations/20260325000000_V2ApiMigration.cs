using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data.Migrations
{
    /// <inheritdoc />
    public partial class V2ApiMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename GelatoTaskId → GaslessTxHash
            migrationBuilder.RenameColumn(
                name: "GelatoTaskId",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                newName: "GaslessTxHash");

            // Add new columns
            migrationBuilder.AddColumn<string>(
                name: "SourceChain",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetChain",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvmHtlcAddress",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAmountRaw",
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
                name: "SourceAmountRaw",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");

            migrationBuilder.DropColumn(
                name: "EvmHtlcAddress",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");

            migrationBuilder.DropColumn(
                name: "TargetChain",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");

            migrationBuilder.DropColumn(
                name: "SourceChain",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");

            migrationBuilder.RenameColumn(
                name: "GaslessTxHash",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                newName: "GelatoTaskId");
        }
    }
}
