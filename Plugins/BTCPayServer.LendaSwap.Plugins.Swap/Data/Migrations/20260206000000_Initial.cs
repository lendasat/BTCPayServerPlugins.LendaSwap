using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.LendaSwap.Plugins.Swap");

            migrationBuilder.CreateTable(
                name: "SwapRecords",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SwapType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AmountSats = table.Column<long>(type: "bigint", nullable: false),
                    PreimageEncrypted = table.Column<string>(type: "text", nullable: true),
                    PaymentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LendaSwapId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PaymentAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClaimDestination = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HtlcExpiryBlock = table.Column<long>(type: "bigint", nullable: true),
                    GelatoTaskId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwapRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LendaSwapSettings",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                columns: table => new
                {
                    StoreId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Setting = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendaSwapSettings", x => x.StoreId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwapRecords_StoreId",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_SwapRecords_LendaSwapId",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                column: "LendaSwapId");

            migrationBuilder.CreateIndex(
                name: "IX_SwapRecords_Status",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap",
                table: "SwapRecords",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwapRecords",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap");

            migrationBuilder.DropTable(
                name: "LendaSwapSettings",
                schema: "BTCPayServer.LendaSwap.Plugins.Swap");
        }
    }
}
