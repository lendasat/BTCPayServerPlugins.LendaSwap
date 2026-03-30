using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEvmCoordinatorAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""BTCPayServer.LendaSwap.Plugins.Swap"".""SwapRecords""
                    ADD COLUMN IF NOT EXISTS ""EvmCoordinatorAddress"" character varying(128),
                    ADD COLUMN IF NOT EXISTS ""EvmDepositAddress"" character varying(128),
                    ADD COLUMN IF NOT EXISTS ""EvmGasless"" boolean NOT NULL DEFAULT false;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvmCoordinatorAddress",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords");
        }
    }
}
