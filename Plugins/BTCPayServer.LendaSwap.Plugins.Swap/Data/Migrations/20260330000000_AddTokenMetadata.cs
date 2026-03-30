using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""BTCPayServer.LendaSwap.Plugins.Swap"".""SwapRecords""
                    ADD COLUMN IF NOT EXISTS ""SourceTokenSymbol"" character varying(32),
                    ADD COLUMN IF NOT EXISTS ""SourceTokenDecimals"" integer,
                    ADD COLUMN IF NOT EXISTS ""TargetTokenSymbol"" character varying(32),
                    ADD COLUMN IF NOT EXISTS ""TargetTokenDecimals"" integer,
                    ADD COLUMN IF NOT EXISTS ""TargetAmountRaw"" character varying(128);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SourceTokenSymbol", schema: "BTCPayServer.LendaSwap.Plugins.Swap", table: "SwapRecords");
            migrationBuilder.DropColumn(name: "SourceTokenDecimals", schema: "BTCPayServer.LendaSwap.Plugins.Swap", table: "SwapRecords");
            migrationBuilder.DropColumn(name: "TargetTokenSymbol", schema: "BTCPayServer.LendaSwap.Plugins.Swap", table: "SwapRecords");
            migrationBuilder.DropColumn(name: "TargetTokenDecimals", schema: "BTCPayServer.LendaSwap.Plugins.Swap", table: "SwapRecords");
            migrationBuilder.DropColumn(name: "TargetAmountRaw", schema: "BTCPayServer.LendaSwap.Plugins.Swap", table: "SwapRecords");
        }
    }
}
