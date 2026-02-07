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

    [MaxLength(128)]
    public string GelatoTaskId { get; set; }

    [MaxLength(128)]
    public string TxId { get; set; }

    [MaxLength(130)]
    public string RefundPubKeyHex { get; set; }

    [MaxLength(1000)]
    public string ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SwapRecord>()
            .HasIndex(s => s.StoreId);

        modelBuilder.Entity<SwapRecord>()
            .HasIndex(s => s.LendaSwapId);

        modelBuilder.Entity<SwapRecord>()
            .HasIndex(s => s.Status);
    }
}

public enum SwapType
{
    LightningToUsdc,
    BitcoinToArkade
}

public enum SwapStatus
{
    Created = 0,
    PendingPayment = 1,
    PendingClaim = 2,
    Claiming = 3,
    Completed = 4,
    Failed = 5,
    Expired = 6,
    PayingFromWallet = 7
}
