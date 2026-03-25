using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class SwapStatusMonitor(
    PluginDbContextFactory dbContextFactory,
    LendaSwapApiClient apiClient,
    IServiceScopeFactory serviceScopeFactory,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<SwapStatusMonitor> logger) : IPeriodicTask
{
    private static readonly SwapStatus[] TerminalStatuses =
    [
        SwapStatus.Completed, SwapStatus.Failed, SwapStatus.Expired
    ];

    public async Task Do(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = dbContextFactory.CreateContext();
            var activeSwaps = await db.SwapRecords
                .Where(s => !TerminalStatuses.Contains(s.Status))
                .ToListAsync(cancellationToken);

            foreach (var swap in activeSwaps)
            {
                try
                {
                    await PollAndUpdateSwap(db, swap, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to poll swap {SwapId}", swap.Id);
                }
            }
        }
        catch (Exception ex) when (ex is Npgsql.PostgresException or InvalidOperationException)
        {
            logger.LogDebug("SwapStatusMonitor: DB not ready yet, skipping cycle");
        }
    }

    private async Task PollAndUpdateSwap(PluginDbContext db, SwapRecord swap, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(swap.LendaSwapId))
            return;

        var remote = await apiClient.GetSwapStatus(swap.LendaSwapId, ct);
        var remoteStatus = remote.Status?.ToLowerInvariant();

        var now = DateTimeOffset.UtcNow;
        swap.UpdatedAt = now;

        switch (remoteStatus)
        {
            case "pending":
                swap.Status = SwapStatus.PendingPayment;
                break;

            case "clientfundingseen":
            case "clientfunded":
                // BTC→EVM/Arkade: plugin paid LN/onchain → waiting for server to fund their side
                // EVM→BTC: user funded EVM HTLC → server is processing
                swap.Status = swap.SwapType switch
                {
                    SwapType.EvmToLightning or SwapType.EvmToBitcoin => SwapStatus.Processing,
                    // For LN→EVM, clientfunded means our LN payment arrived, server will fund EVM next
                    SwapType.LightningToEvm or SwapType.LightningToUsdc => SwapStatus.Processing,
                    _ => SwapStatus.PendingPayment
                };
                break;

            case "serverfunded":
                switch (swap.SwapType)
                {
                    case SwapType.EvmToLightning:
                        // Server funded Boltz VHTLC → Boltz will pay our LN invoice automatically.
                        // Nothing for us to do — wait for serverredeemed.
                        swap.Status = SwapStatus.Processing;
                        break;

                    case SwapType.EvmToBitcoin:
                        // Server sent BTC to Taproot HTLC → auto-claim!
                        // FIX: Set Claiming BEFORE attempt to prevent race condition
                        if (swap.Status is not (SwapStatus.Claiming or SwapStatus.Completed))
                        {
                            swap.Status = SwapStatus.Claiming;
                            await db.SaveChangesAsync(ct);
                            await TryAutoClaimBtcHtlc(swap, remote, ct);
                        }
                        break;

                    case SwapType.LightningToEvm or SwapType.LightningToUsdc:
                        // Server funded EVM HTLC → auto-claim gaslessly!
                        // FIX: Set Claiming BEFORE attempt to prevent race condition
                        if (swap.Status is not (SwapStatus.Claiming or SwapStatus.Completed))
                        {
                            swap.Status = SwapStatus.Claiming;
                            await db.SaveChangesAsync(ct);
                            await TryAutoClaimEvmHtlc(swap, remote, ct);
                        }
                        break;

                    default:
                        // BTC→Arkade, LN→Arkade: server funded, user claims client-side
                        swap.Status = SwapStatus.PendingClaim;
                        break;
                }
                break;

            case "clientredeeming":
                swap.Status = SwapStatus.Claiming;
                break;

            case "clientredeemed":
                // FIX: Handle EvmToLightning and EvmToBitcoin separately
                if (swap.SwapType == SwapType.EvmToBitcoin)
                {
                    // User claimed BTC HTLC → server still needs to claim EVM HTLC
                    swap.Status = SwapStatus.Claiming;
                }
                else
                {
                    // All other flows: clientredeemed = done (or nearly done)
                    swap.Status = SwapStatus.Completed;
                    swap.CompletedAt = now;
                }
                break;

            case "serverredeemed":
                // Terminal for all flows: server claimed their side
                swap.Status = SwapStatus.Completed;
                swap.CompletedAt = now;
                break;

            case "clientredeemedandclientrefunded":
                swap.Status = SwapStatus.Completed;
                swap.CompletedAt = now;
                break;

            case "expired":
                swap.Status = SwapStatus.Expired;
                break;

            case "clientrefunded":
            case "clientfundedserverrefunded":
            case "clientrefundedserverfunded":
            case "clientrefundedserverrefunded":
            case "clientinvalidfunded":
            case "clientfundedtoolate":
                swap.Status = SwapStatus.Failed;
                swap.ErrorMessage = $"Swap ended with status: {remoteStatus}";
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Attempts to automatically claim BTC from the Taproot HTLC for an EVM→Bitcoin swap.
    /// Uses a separate DI scope to avoid DbContext contamination.
    /// </summary>
    private async Task TryAutoClaimBtcHtlc(SwapRecord swap, GetSwapResponse remote, CancellationToken ct)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var storeRepo = scope.ServiceProvider.GetRequiredService<StoreRepository>();
            var store = await storeRepo.FindStore(swap.StoreId);
            if (store == null)
            {
                logger.LogWarning("Store {StoreId} not found for swap {SwapId}", swap.StoreId, swap.Id);
                await RevertClaimStatus(swap, "Store not found");
                return;
            }

            var claimService = scope.ServiceProvider.GetRequiredService<TaprootHtlcClaimService>();
            var (success, txId, error) = await claimService.TryClaimBtcHtlc(store, swap, remote, ct);

            // Update swap in a fresh DB context to avoid contamination
            await using var db = dbContextFactory.CreateContext();
            db.SwapRecords.Attach(swap);

            if (success)
            {
                swap.TxId = txId;
                swap.ErrorMessage = null;
                logger.LogInformation("BTC HTLC auto-claimed for swap {SwapId}, TxId: {TxId}", swap.Id, txId);
            }
            else
            {
                // Revert to PendingClaim so it can be retried next cycle
                swap.Status = SwapStatus.PendingClaim;
                swap.ErrorMessage = $"Auto-claim failed: {error}";
                logger.LogWarning("BTC HTLC auto-claim failed for swap {SwapId}: {Error}", swap.Id, error);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during BTC HTLC auto-claim for swap {SwapId}", swap.Id);
            await RevertClaimStatus(swap, $"Auto-claim error: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to gaslessly claim tokens from an EVM HTLC for a Lightning→EVM swap.
    /// Uses the plugin's derived EVM key to sign the EIP-712 Redeem message.
    /// </summary>
    private async Task TryAutoClaimEvmHtlc(SwapRecord swap, GetSwapResponse remote, CancellationToken ct)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var storeRepo = scope.ServiceProvider.GetRequiredService<StoreRepository>();
            var store = await storeRepo.FindStore(swap.StoreId);
            if (store == null)
            {
                logger.LogWarning("Store {StoreId} not found for swap {SwapId}", swap.StoreId, swap.Id);
                await RevertClaimStatus(swap, "Store not found");
                return;
            }

            var evmClaimService = scope.ServiceProvider.GetRequiredService<EvmGaslessClaimService>();
            var (success, txHash, error) = await evmClaimService.TryGaslessClaim(store, swap, remote, ct);

            await using var db = dbContextFactory.CreateContext();
            db.SwapRecords.Attach(swap);

            if (success)
            {
                swap.GaslessTxHash = txHash;
                swap.ErrorMessage = null;
                logger.LogInformation("Gasless EVM claim triggered for swap {SwapId}, TxHash: {TxHash}",
                    swap.Id, txHash);
            }
            else
            {
                swap.Status = SwapStatus.PendingClaim;
                swap.ErrorMessage = $"Gasless claim failed: {error}";
                logger.LogWarning("Gasless EVM claim failed for swap {SwapId}: {Error}", swap.Id, error);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during gasless EVM claim for swap {SwapId}", swap.Id);
            await RevertClaimStatus(swap, $"Gasless claim error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reverts a swap from Claiming back to PendingClaim after a failed attempt.
    /// </summary>
    private async Task RevertClaimStatus(SwapRecord swap, string errorMessage)
    {
        try
        {
            await using var db = dbContextFactory.CreateContext();
            db.SwapRecords.Attach(swap);
            swap.Status = SwapStatus.PendingClaim;
            swap.ErrorMessage = errorMessage;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revert claim status for swap {SwapId}", swap.Id);
        }
    }
}
