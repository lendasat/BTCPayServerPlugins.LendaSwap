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
            DefaultEvmAddress = settings.DefaultEvmAddress
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
            DefaultEvmAddress = model.DefaultEvmAddress
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

        TokenInfosResponse tokens;
        try
        {
            tokens = await apiClient.GetTokens();
        }
        catch
        {
            tokens = new TokenInfosResponse();
        }

        var (hasLightning, hasOnchain, hasHotWallet) = swapService.GetWalletStatus(CurrentStore);
        ViewData["HasLightning"] = hasLightning;
        ViewData["HasOnchain"] = hasOnchain;
        ViewData["HasOnchainHotWallet"] = hasHotWallet;
        ViewData["DefaultEvmAddress"] = storeSettings.DefaultEvmAddress ?? storeSettings.DefaultPolygonAddress ?? "";
        ViewData["DefaultArkadeAddress"] = storeSettings.DefaultArkadeAddress ?? "";

        var model = new CreateSwapViewModel
        {
            SourceChain = "Lightning",
            SourceToken = "btc",
            TargetChain = "Polygon",
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

        var (hasLightning, hasOnchain, hasHotWallet) = swapService.GetWalletStatus(CurrentStore);
        ViewData["HasLightning"] = hasLightning;
        ViewData["HasOnchain"] = hasOnchain;
        ViewData["HasOnchainHotWallet"] = hasHotWallet;

        try
        {
            var tokens = await apiClient.GetTokens();
            model.BtcTokens = tokens.BtcTokens;
            model.EvmTokens = tokens.EvmTokens;
        }
        catch
        {
            model.BtcTokens = new List<TokenInfo>();
            model.EvmTokens = new List<TokenInfo>();
        }

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
                // Validation error (e.g. no wallet) — re-show form
                ModelState.AddModelError("", createError);
                return View(nameof(Send), model);
            }

            // Swap was saved but API call failed
            return View(nameof(Send), model);
        }

        // For EVM→BTC flows, the user funds EVM manually — no auto-pay from plugin
        if (swap.SwapType is SwapType.EvmToLightning or SwapType.EvmToBitcoin)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Swap created. Please fund the EVM HTLC from your wallet to complete the swap.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        // For BTC→EVM/Arkade flows, try auto-pay from store's wallet
        else if (swap.Status == SwapStatus.PendingPayment)
        {
            var (paid, _) = await swapService.TryAutoPayAsync(CurrentStore, swap, ct);

            if (paid)
            {
                var successMsg = swap.SwapType is SwapType.LightningToEvm or SwapType.LightningToUsdc or SwapType.LightningToArkade
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
            catch
            {
                // Decryption failed — preimage not available
            }
        }

        return View(swap);
    }

    [HttpPost("{swapId}/retry-claim")]
    public async Task<IActionResult> RetryClaim(string storeId, string swapId)
    {
        if (CurrentStore is null)
            return NotFound();

        await using var db = dbContextFactory.CreateContext();
        var swap = await db.SwapRecords
            .FirstOrDefaultAsync(s => s.Id == swapId && s.StoreId == storeId);

        if (swap is null)
            return NotFound();

        // Gasless claim for EVM swaps now requires EIP-712 signature (EVM private key).
        // The BTCPay plugin cannot auto-claim EVM swaps — the user must claim from their EVM wallet.
        if (swap.SwapType is SwapType.LightningToEvm or SwapType.LightningToUsdc)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "EVM claims require an EIP-712 signature from your EVM wallet. Please claim from your wallet directly.",
                Severity = StatusMessageModel.StatusSeverity.Warning
            });
            return RedirectToAction(nameof(SwapDetail), new { storeId, swapId });
        }

        if (swap.Status is not (SwapStatus.PendingClaim or SwapStatus.Claiming))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Retry is only available for swaps in PendingClaim or Claiming status.",
                Severity = StatusMessageModel.StatusSeverity.Warning
            });
            return RedirectToAction(nameof(SwapDetail), new { storeId, swapId });
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = "Claim must be done from the client wallet for this swap type.",
            Severity = StatusMessageModel.StatusSeverity.Info
        });

        return RedirectToAction(nameof(SwapDetail), new { storeId, swapId });
    }
}
