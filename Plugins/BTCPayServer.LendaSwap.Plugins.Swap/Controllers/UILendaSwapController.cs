using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.Services;
using BTCPayServer.LendaSwap.Plugins.Swap.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Controllers;

[Route("~/plugins/{storeId}/lendaswap")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UILendaSwapController(
    PluginDbContextFactory dbContextFactory,
    LendaSwapApiClient apiClient,
    SwapService swapService,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<UILendaSwapController> logger) : Controller
{
    private StoreData CurrentStore => HttpContext.GetStoreData();

    private IDataProtector Protector =>
        dataProtectionProvider.CreateProtector("LendaSwap.Preimage");

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var settings = await dbContextFactory.GetSettingAsync(storeId);
        var model = new LendaSwapSettingsViewModel
        {
            DefaultPolygonAddress = settings.DefaultPolygonAddress,
            DefaultArkadeAddress = settings.DefaultArkadeAddress,
            DefaultEvmAddress = settings.DefaultEvmAddress,
            ApiBaseUrl = settings.ApiBaseUrl
        };
        return View(model);
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(string storeId, LendaSwapSettingsViewModel model)
    {
        if (CurrentStore is null)
            return NotFound();

        if (!ModelState.IsValid)
            return View(model);

        var settings = new LendaSwapSettings
        {
            DefaultPolygonAddress = model.DefaultPolygonAddress,
            DefaultArkadeAddress = model.DefaultArkadeAddress,
            DefaultEvmAddress = model.DefaultEvmAddress,
            ApiBaseUrl = model.ApiBaseUrl
        };

        await dbContextFactory.SetSettingAsync(storeId, settings);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = "LendaSwap settings updated successfully",
            Severity = StatusMessageModel.StatusSeverity.Success
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpGet("send")]
    public async Task<IActionResult> Send(string storeId)
    {
        var storeSettings = await dbContextFactory.GetSettingAsync(storeId);
        PopulateWalletStatus();
        ViewData["DefaultEvmAddress"] = storeSettings.DefaultEvmAddress ?? storeSettings.DefaultPolygonAddress ?? "";
        ViewData["DefaultArkadeAddress"] = storeSettings.DefaultArkadeAddress ?? "";

        var tokens = await FetchTokensSafe();
        var model = new CreateSwapViewModel
        {
            SourceChain = "Lightning",
            SourceToken = "btc",
            TargetChain = "137",
            TargetToken = "",
            ClaimDestination = storeSettings.DefaultEvmAddress ?? storeSettings.DefaultPolygonAddress,
            BtcTokens = tokens.BtcTokens,
            EvmTokens = tokens.EvmTokens
        };
        return View(model);
    }

    [HttpPost("send")]
    public async Task<IActionResult> SendCreate(string storeId, CreateSwapViewModel model)
    {
        if (CurrentStore is null)
            return NotFound();

        PopulateWalletStatus();
        await PopulateTokens(model);

        if (!ModelState.IsValid)
            return View(nameof(Send), model);

        var ct = HttpContext.RequestAborted;
        var (swap, createError) = await swapService.CreateSwapAsync(CurrentStore, storeId, model, ct);

        if (createError != null)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = createError,
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            if (swap == null)
            {
                ModelState.AddModelError("", createError);
                return View(nameof(Send), model);
            }
            return View(nameof(Send), model);
        }

        if (swap.SwapType is SwapType.EvmToLightning or SwapType.EvmToBitcoin)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Swap created. Please fund the EVM HTLC from your wallet to complete the swap.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        else if (swap.Status == SwapStatus.PendingPayment)
        {
            var (paid, _) = await swapService.TryAutoPayAsync(CurrentStore, swap, ct);
            if (paid)
            {
                var successMsg = swap.SwapType is SwapType.LightningToEvm or SwapType.LightningToArkade
                    ? "Swap created and paid via Lightning. Waiting for claim."
                    : "Swap created and payment broadcast from hot wallet.";
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = successMsg,
                    Severity = StatusMessageModel.StatusSeverity.Success
                });
            }
            else
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "Swap created. Auto-pay was not possible — please pay manually.",
                    Severity = StatusMessageModel.StatusSeverity.Warning
                });
            }
        }

        return RedirectToAction(nameof(SwapDetail), new { storeId, swapId = swap.Id });
    }

    [HttpGet("receive")]
    public async Task<IActionResult> Receive(string storeId)
    {
        PopulateWalletStatus();

        var tokens = await FetchTokensSafe();
        var model = new CreateSwapViewModel
        {
            SourceChain = "137",
            SourceToken = "",
            TargetChain = "Lightning",
            TargetToken = "btc",
            BtcTokens = tokens.BtcTokens,
            EvmTokens = tokens.EvmTokens
        };
        return View(model);
    }

    [HttpPost("receive")]
    public async Task<IActionResult> ReceiveCreate(string storeId, CreateSwapViewModel model)
    {
        if (CurrentStore is null)
            return NotFound();

        PopulateWalletStatus();
        await PopulateTokens(model);

        if (!ModelState.IsValid)
            return View(nameof(Receive), model);

        var ct = HttpContext.RequestAborted;
        var (swap, createError) = await swapService.CreateSwapAsync(CurrentStore, storeId, model, ct);

        if (createError != null)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = createError,
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            if (swap == null)
            {
                ModelState.AddModelError("", createError);
                return View(nameof(Receive), model);
            }
            return View(nameof(Receive), model);
        }

        var successMsg = swap.SwapType == SwapType.EvmToLightning
            ? "Swap created. Fund the EVM HTLC to receive BTC on Lightning automatically."
            : "Swap created. Fund the EVM HTLC to receive BTC onchain.";

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = successMsg,
            Severity = StatusMessageModel.StatusSeverity.Success
        });

        return RedirectToAction(nameof(SwapDetail), new { storeId, swapId = swap.Id });
    }

    private void PopulateWalletStatus()
    {
        var (hasLightning, hasOnchain, hasHotWallet) = swapService.GetWalletStatus(CurrentStore);
        ViewData["HasLightning"] = hasLightning;
        ViewData["HasOnchain"] = hasOnchain;
        ViewData["HasOnchainHotWallet"] = hasHotWallet;
    }

    private async Task<TokenInfosResponse> FetchTokensSafe()
    {
        try
        {
            return await apiClient.GetTokens();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch tokens from LendaSwap API");
            return new TokenInfosResponse();
        }
    }

    private async Task PopulateTokens(CreateSwapViewModel model)
    {
        var tokens = await FetchTokensSafe();
        model.BtcTokens = tokens.BtcTokens;
        model.EvmTokens = tokens.EvmTokens;
    }

    [HttpGet("")]
    public async Task<IActionResult> SwapList(string storeId, SwapStatus? statusFilter, int page = 1)
    {
        await using var db = dbContextFactory.CreateContext();

        var query = db.SwapRecords
            .Where(s => s.StoreId == storeId)
            .AsNoTracking();

        if (statusFilter.HasValue)
            query = query.Where(s => s.Status == statusFilter.Value);

        var totalCount = await query.CountAsync();
        var pageSize = 25;

        var swaps = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var model = new SwapListViewModel
        {
            Swaps = swaps,
            StatusFilter = statusFilter,
            CurrentPage = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };

        return View(model);
    }

    [HttpGet("api/quote")]
    public async Task<IActionResult> GetQuoteApi(
        string storeId, string sourceChain, string sourceToken,
        string targetChain, string targetToken, long amount)
    {
        if (string.IsNullOrEmpty(sourceChain) || string.IsNullOrEmpty(sourceToken) ||
            string.IsNullOrEmpty(targetChain) || string.IsNullOrEmpty(targetToken) || amount <= 0)
            return BadRequest(new { error = "Invalid parameters" });

        try
        {
            var quote = await apiClient.GetQuote(sourceChain, sourceToken, targetChain, targetToken, sourceAmount: amount);
            return Json(new
            {
                exchangeRate = quote.ExchangeRate,
                protocolFee = quote.ProtocolFee,
                networkFee = quote.NetworkFee,
                gaslessNetworkFee = quote.GaslessNetworkFee,
                minAmount = quote.MinAmount,
                maxAmount = quote.MaxAmount,
                protocolFeeRate = quote.ProtocolFeeRate,
                sourceAmount = quote.SourceAmountCalculated,
                targetAmount = quote.TargetAmountCalculated
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{swapId}")]
    public async Task<IActionResult> SwapDetail(string storeId, string swapId)
    {
        await using var db = dbContextFactory.CreateContext();
        var swap = await db.SwapRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == swapId && s.StoreId == storeId);

        if (swap is null)
            return NotFound();

        if (!string.IsNullOrEmpty(swap.PreimageEncrypted) &&
            swap.Status is not (SwapStatus.Created or SwapStatus.PendingPayment))
        {
            try
            {
                ViewData["Preimage"] = Protector.Unprotect(swap.PreimageEncrypted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to decrypt preimage for swap {SwapId} — DataProtection keys may have rotated", swap.Id);
                ViewData["PreimageError"] = "Preimage could not be decrypted. DataProtection keys may have changed.";
            }
        }

        return View(swap);
    }

    /// <summary>
    /// JSON endpoint for AJAX polling — returns swap status without a full page reload.
    /// </summary>
    [HttpGet("{swapId}/status")]
    public async Task<IActionResult> SwapStatusJson(string storeId, string swapId)
    {
        await using var db = dbContextFactory.CreateContext();
        var swap = await db.SwapRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == swapId && s.StoreId == storeId);

        if (swap is null)
            return NotFound();

        return Json(new
        {
            status = swap.Status.ToString(),
            statusInt = (int)swap.Status,
            errorMessage = swap.ErrorMessage,
            txId = swap.TxId,
            gaslessTxHash = swap.GaslessTxHash,
            updatedAt = swap.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            completedAt = swap.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            isTerminal = swap.Status is SwapStatus.Completed or SwapStatus.Failed or SwapStatus.Expired
        });
    }
}
