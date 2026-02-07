using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class SwapStatusMonitor(
    PluginDbContextFactory dbContextFactory,
    LendaSwapApiClient apiClient,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<SwapStatusMonitor> logger) : IPeriodicTask
{
    private static readonly SwapStatus[] TerminalStatuses =
    [
        SwapStatus.Completed, SwapStatus.Failed, SwapStatus.Expired
    ];

    private IDataProtector Protector =>
        dataProtectionProvider.CreateProtector("LendaSwap.Preimage");

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
                swap.Status = SwapStatus.PendingPayment;
                break;

            case "serverfunded":
                if (swap.SwapType == SwapType.LightningToUsdc &&
                    swap.Status != SwapStatus.Claiming)
                {
                    swap.Status = SwapStatus.PendingClaim;
                    await TryClaimViaGelato(db, swap, ct);
                }
                else
                {
                    swap.Status = SwapStatus.PendingClaim;
                }
                break;

            case "clientredeeming":
                swap.Status = SwapStatus.Claiming;
                break;

            case "clientredeemed":
            case "serverredeemed":
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

    private async Task TryClaimViaGelato(PluginDbContext db, SwapRecord swap, CancellationToken ct)
    {
        try
        {
            var preimage = Protector.Unprotect(swap.PreimageEncrypted);
            var result = await apiClient.ClaimViaGelato(swap.LendaSwapId, preimage, ct);

            swap.Status = SwapStatus.Claiming;
            swap.GelatoTaskId = result.TaskId;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Gelato claim triggered for swap {SwapId}, task {TaskId}",
                swap.Id, result.TaskId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger Gelato claim for swap {SwapId}", swap.Id);
            swap.ErrorMessage = $"Gelato claim failed: {ex.Message}";
        }
    }
}
