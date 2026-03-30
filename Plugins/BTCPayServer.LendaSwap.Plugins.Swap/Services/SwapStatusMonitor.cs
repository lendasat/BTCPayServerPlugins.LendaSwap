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

    /// <summary>
    /// Auto-expire swaps stuck in non-terminal status for longer than this.
    /// </summary>
    private static readonly TimeSpan SwapStaleTimeout = TimeSpan.FromHours(24);

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
                    // Auto-expire stale swaps that have been stuck too long
                    if (DateTimeOffset.UtcNow - swap.CreatedAt > SwapStaleTimeout
                        && swap.Status is SwapStatus.Created or SwapStatus.PendingPayment)
                    {
                        swap.Status = SwapStatus.Expired;
                        swap.ErrorMessage = "Swap expired — no activity within 24 hours.";
                        swap.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);
                        logger.LogInformation("Auto-expired stale swap {SwapId} (created {CreatedAt})",
                            swap.Id, swap.CreatedAt);
                        continue;
                    }

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
                // For gasless EVM→BTC/Lightning swaps, the server doesn't report "depositseen" —
                // it waits for the plugin to call fund-gasless. Try funding on every poll cycle;
                // the call is idempotent and fails gracefully if the deposit hasn't arrived yet.
                if (swap.EvmGasless && swap.Status == SwapStatus.PendingPayment
                    && swap.SwapType is SwapType.EvmToBitcoin or SwapType.EvmToLightning)
                {
                    await TryAutoFundGasless(swap, ct);
                }
                else
                {
                    swap.Status = SwapStatus.PendingPayment;
                }
                break;

            case "depositseen":
            case "depositconfirmed":
                // Gasless EVM→BTC: tokens arrived at deposit address → auto-fund via Permit2
                if (swap.EvmGasless && swap.Status == SwapStatus.PendingPayment)
                {
                    logger.LogInformation("Tokens deposited for gasless swap {SwapId}, triggering auto-fund", swap.Id);
                    swap.Status = SwapStatus.Processing;
                    await db.SaveChangesAsync(ct);
                    await TryAutoFundGasless(swap, ct);
                }
                break;

            case "clientfundingseen":
            case "clientfunded":
                // BTC→EVM/Arkade: plugin paid LN/onchain → waiting for server to fund their side
                // EVM→BTC: user funded EVM HTLC → server is processing
                swap.Status = swap.SwapType switch
                {
                    SwapType.EvmToLightning or SwapType.EvmToBitcoin => SwapStatus.Processing,
                    SwapType.LightningToEvm or SwapType.BitcoinToEvm => SwapStatus.Processing,
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
                        if (swap.Status is not (SwapStatus.Claiming or SwapStatus.Completed))
                        {
                            swap.Status = SwapStatus.Claiming;
                            await db.SaveChangesAsync(ct);
                            await TryAutoClaimBtcHtlc(swap, remote, ct);
                        }
                        break;

                    case SwapType.LightningToEvm or SwapType.BitcoinToEvm:
                        // Server funded EVM HTLC → auto-claim gaslessly!
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
                if (swap.SwapType == SwapType.EvmToBitcoin)
                {
                    // User claimed BTC HTLC → server still needs to claim EVM HTLC
                    swap.Status = SwapStatus.Claiming;
                }
                else
                {
                    swap.Status = SwapStatus.Completed;
                    swap.CompletedAt = now;
                }
                break;

            case "serverredeemed":
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

            default:
                logger.LogWarning("Unknown remote status '{RemoteStatus}' for swap {SwapId}", remoteStatus, swap.Id);
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Signs Permit2 and calls fund-gasless to move tokens from deposit address into the HTLC.
    /// </summary>
    private async Task TryAutoFundGasless(SwapRecord swap, CancellationToken ct)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var storeRepo = scope.ServiceProvider.GetRequiredService<StoreRepository>();
            var store = await storeRepo.FindStore(swap.StoreId);
            if (store == null)
            {
                logger.LogWarning("Store {StoreId} not found for gasless fund {SwapId}", swap.StoreId, swap.Id);
                return;
            }

            var evmClaimService = scope.ServiceProvider.GetRequiredService<EvmGaslessClaimService>();
            var (success, txHash, error) = await evmClaimService.TryGaslessFund(store, swap, ct);

            await using var db = dbContextFactory.CreateContext();
            db.SwapRecords.Attach(swap);

            if (success)
            {
                swap.GaslessTxHash = txHash;
                swap.ErrorMessage = null;
                logger.LogInformation("Gasless fund succeeded for swap {SwapId}, TxHash: {TxHash}", swap.Id, txHash);
            }
            else
            {
                swap.Status = SwapStatus.PendingPayment;
                swap.ErrorMessage = $"Gasless fund failed: {error}";
                logger.LogWarning("Gasless fund failed for swap {SwapId}: {Error}", swap.Id, error);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during gasless fund for swap {SwapId}", swap.Id);
        }
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
