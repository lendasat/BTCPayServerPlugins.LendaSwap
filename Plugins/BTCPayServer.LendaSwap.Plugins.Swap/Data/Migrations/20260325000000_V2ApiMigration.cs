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
            // All operations use raw SQL with IF NOT EXISTS for idempotency.
            // This handles cases where previous migrations without Designer files were skipped.
            migrationBuilder.Sql(@"
                ALTER TABLE ""BTCPayServer.LendaSwap.Plugins.Swap"".""SwapRecords""
                    ADD COLUMN IF NOT EXISTS ""RefundPubKeyHex"" character varying(130),
                    ADD COLUMN IF NOT EXISTS ""SourceChain"" character varying(32),
                    ADD COLUMN IF NOT EXISTS ""TargetChain"" character varying(32),
                    ADD COLUMN IF NOT EXISTS ""EvmHtlcAddress"" character varying(512),
                    ADD COLUMN IF NOT EXISTS ""SourceAmountRaw"" character varying(128),
                    ADD COLUMN IF NOT EXISTS ""TxId"" character varying(128);

                -- Rename GelatoTaskId → GaslessTxHash if the old column still exists
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = 'BTCPayServer.LendaSwap.Plugins.Swap'
                               AND table_name = 'SwapRecords'
                               AND column_name = 'GelatoTaskId') THEN
                        ALTER TABLE ""BTCPayServer.LendaSwap.Plugins.Swap"".""SwapRecords""
                        RENAME COLUMN ""GelatoTaskId"" TO ""GaslessTxHash"";
                    END IF;
                END $$;

                -- Ensure GaslessTxHash exists even if GelatoTaskId never existed
                ALTER TABLE ""BTCPayServer.LendaSwap.Plugins.Swap"".""SwapRecords""
                    ADD COLUMN IF NOT EXISTS ""GaslessTxHash"" character varying(128);
            ");
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
