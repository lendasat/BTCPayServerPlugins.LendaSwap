using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data;

public class SwapRecord
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string StoreId { get; set; }

    public SwapType SwapType { get; set; }

    public SwapStatus Status { get; set; }

    public long AmountSats { get; set; }

    public string PreimageEncrypted { get; set; }

    [MaxLength(128)]
    public string PaymentHash { get; set; }

    [MaxLength(128)]
    public string LendaSwapId { get; set; }

    [MaxLength(512)]
    public string PaymentAddress { get; set; }

    [MaxLength(256)]
    public string ClaimDestination { get; set; }

    [MaxLength(64)]
    public string SourceToken { get; set; }

    [MaxLength(64)]
    public string TargetToken { get; set; }

    [MaxLength(512)]
    public string TargetHtlcAddress { get; set; }

    public long? HtlcExpiryBlock { get; set; }

    /// <summary>
    /// Gasless claim transaction hash (was GelatoTaskId in v1).
    /// </summary>
    [MaxLength(128)]
    public string GaslessTxHash { get; set; }

    [MaxLength(128)]
    public string TxId { get; set; }

    [MaxLength(130)]
    public string RefundPubKeyHex { get; set; }

    /// <summary>
    /// For EVM→BTC flows: the EVM HTLC contract address that the user must fund.
    /// Stores the token amount the user needs to send (in source token smallest units).
    /// </summary>
    [MaxLength(512)]
    public string EvmHtlcAddress { get; set; }

    /// <summary>
    /// Source amount in token's smallest units (for EVM→BTC: how much EVM tokens to send).
    /// </summary>
    [MaxLength(128)]
    public string SourceAmountRaw { get; set; }

    [MaxLength(1000)]
    public string ErrorMessage { get; set; }

    /// <summary>
    /// Source chain identifier (e.g. "Lightning", "Bitcoin", "Arkade").
    /// </summary>
    [MaxLength(32)]
    public string SourceChain { get; set; }

    /// <summary>
    /// Target chain identifier (e.g. "137" for Polygon, "1" for Ethereum, "Arkade").
    /// </summary>
    [MaxLength(32)]
    public string TargetChain { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Composite index for the swap list query (filter by store, order by date)
        modelBuilder.Entity<SwapRecord>()
            .HasIndex(s => new { s.StoreId, s.CreatedAt });

        modelBuilder.Entity<SwapRecord>()
            .HasIndex(s => s.LendaSwapId);

        modelBuilder.Entity<SwapRecord>()
            .HasIndex(s => s.Status);
    }
}

public enum SwapType
{
    [System.Obsolete("Use LightningToEvm instead. Kept for backward compatibility with existing DB records.")]
    LightningToUsdc = 0,
    BitcoinToArkade = 1,
    LightningToEvm = 2,
    LightningToArkade = 3,
    BitcoinToEvm = 4,
    EvmToLightning = 5,
    EvmToBitcoin = 6
}

public enum SwapStatus
{
    Created = 0,
    /// <summary>BTC→EVM/Arkade: waiting for plugin to pay LN/onchain. EVM→BTC: waiting for user to fund EVM HTLC.</summary>
    PendingPayment = 1,
    /// <summary>BTC→EVM: server funded EVM HTLC, needs EVM claim. EVM→BTC: server funded BTC HTLC, needs BTC claim.</summary>
    PendingClaim = 2,
    Claiming = 3,
    Completed = 4,
    Failed = 5,
    Expired = 6,
    PayingFromWallet = 7,
    /// <summary>EVM→BTC: user funded EVM, server is processing (paying LN or funding BTC HTLC).</summary>
    Processing = 8
}
