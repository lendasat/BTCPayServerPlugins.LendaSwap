using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.ViewModels;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Fees;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class SwapService(
    PluginDbContextFactory dbContextFactory,
    LendaSwapApiClient apiClient,
    SwapCryptoHelper cryptoHelper,
    IDataProtectionProvider dataProtectionProvider,
    LightningClientFactoryService lightningClientFactory,
    IOptions<LightningNetworkOptions> lightningNetworkOptions,
    BTCPayNetworkProvider networkProvider,
    PaymentMethodHandlerDictionary handlers,
    ExplorerClientProvider explorerClientProvider,
    BTCPayWalletProvider walletProvider,
    IFeeProviderFactory feeProviderFactory,
    ILogger<SwapService> logger)
{
    private IDataProtector Protector =>
        dataProtectionProvider.CreateProtector("LendaSwap.Preimage");

    public (bool hasLightning, bool hasOnchain, bool hasHotWallet) GetWalletStatus(StoreData store)
    {
        var lnPmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnPmId, handlers);
        var hasLightning = lnConfig != null;

        var derivation = store.GetDerivationSchemeSettings(handlers, "BTC");
        var hasOnchain = derivation != null;
        var hasHotWallet = derivation is { IsHotWallet: true };

        return (hasLightning, hasOnchain, hasHotWallet);
    }

    /// <summary>
    /// Derives a compressed public key hex from the store's onchain wallet.
    /// Returns (pubKeyHex, isEphemeral). If no real wallet is available, isEphemeral = true.
    /// </summary>
    public async Task<(string pubKeyHex, bool isEphemeral)> GetSwapPubKeyHex(StoreData store, CancellationToken ct)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var derivation = store.GetDerivationSchemeSettings(handlers, "BTC");

        if (derivation != null)
        {
            var explorerClient = explorerClientProvider.GetExplorerClient("BTC");

            var extKeyStr = await explorerClient.GetMetadataAsync<string>(
                derivation.AccountDerivation, WellknownMetadataKeys.AccountHDKey, ct);

            if (extKeyStr != null)
            {
                var accountKey = ExtKey.Parse(extKeyStr, network.NBitcoinNetwork);
                var pubKey = accountKey.Derive(new KeyPath("0/0")).PrivateKey.PubKey;
                return (pubKey.ToHex(), false);
            }

            // Watch-only wallet — can't get private key for refund
        }

        // No usable wallet
        return (null, true);
    }

    /// <summary>
    /// Creates a swap record, calls the LendaSwap API, and saves to DB.
    /// Returns (swap, errorMessage). If errorMessage is non-null, swap creation failed.
    /// </summary>
    public async Task<(SwapRecord swap, string error)> CreateSwapAsync(
        StoreData store, string storeId, CreateSwapViewModel model, CancellationToken ct)
    {
        var swapType = MapToSwapType(model.SourceToken, model.TargetToken);
        if (swapType is null)
            return (null, "Unsupported swap pair selected.");

        var (pubKeyHex, isEphemeral) = await GetSwapPubKeyHex(store, ct);
        if (isEphemeral)
            return (null, "A Bitcoin wallet must be configured for this store to create swaps. Configure an onchain hot wallet in your store settings.");

        var preimage = cryptoHelper.GeneratePreimage();
        var preimageHex = Convert.ToHexString(preimage).ToLowerInvariant();

        logger.LogInformation("Creating {SwapType} swap for store {StoreId}, amount {Amount} sats",
            swapType, storeId, model.AmountSats);

        var swap = new SwapRecord
        {
            StoreId = storeId,
            SwapType = swapType.Value,
            Status = SwapStatus.Created,
            AmountSats = model.AmountSats,
            ClaimDestination = model.ClaimDestination,
            SourceToken = model.SourceToken,
            TargetToken = model.TargetToken,
            PreimageEncrypted = Protector.Protect(preimageHex),
            RefundPubKeyHex = pubKeyHex,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            var quote = await apiClient.GetQuote(model.SourceToken, model.TargetToken, model.AmountSats, ct);
            var totalFees = quote.ProtocolFee + quote.NetworkFee;

            if (swapType == SwapType.LightningToUsdc)
            {
                var paymentHash = cryptoHelper.ComputePaymentHash(preimage);
                swap.PaymentHash = paymentHash;

                var invoiceAmount = model.AmountSats + totalFees;

                var result = await apiClient.CreateLightningToUsdcSwap(new CreateLightningToPolygonRequest
                {
                    HashLock = "0x" + paymentHash,
                    SourceAmount = invoiceAmount,
                    TargetAddress = model.ClaimDestination,
                    TargetToken = model.TargetToken,
                    UserId = pubKeyHex,
                    RefundPk = pubKeyHex
                }, ct);

                swap.LendaSwapId = result.Id;
                swap.PaymentAddress = result.LnInvoice;
                swap.TargetHtlcAddress = result.HtlcAddressEvm;
                swap.HtlcExpiryBlock = result.EvmRefundLocktime;
                swap.AmountSats = result.SourceAmount;
                swap.Status = SwapStatus.PendingPayment;
            }
            else if (swapType == SwapType.BitcoinToArkade)
            {
                var hash160 = cryptoHelper.ComputeHash160(preimage);
                swap.PaymentHash = hash160;

                var result = await apiClient.CreateBitcoinToArkadeSwap(new CreateBitcoinToArkadeRequest
                {
                    HashLock = hash160,
                    SatsReceive = model.AmountSats,
                    TargetArkadeAddress = model.ClaimDestination,
                    UserId = pubKeyHex,
                    ClaimPk = pubKeyHex,
                    RefundPk = pubKeyHex
                }, ct);

                swap.LendaSwapId = result.Id;
                swap.PaymentAddress = result.BtcHtlcAddress;
                swap.TargetHtlcAddress = result.ArkadeVhtlcAddress;
                swap.HtlcExpiryBlock = result.BtcRefundLocktime;
                swap.AmountSats = result.SourceAmount;
                swap.Status = SwapStatus.PendingPayment;
            }
        }
        catch (Exception ex)
        {
            swap.Status = SwapStatus.Failed;
            swap.ErrorMessage = ex.Message;
        }

        await using var db = dbContextFactory.CreateContext();
        db.SwapRecords.Add(swap);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Swap {SwapId} created, LendaSwap ID: {LendaSwapId}, status: {Status}",
            swap.Id, swap.LendaSwapId, swap.Status);

        if (swap.Status == SwapStatus.Failed)
            return (swap, $"Failed to create swap: {swap.ErrorMessage}");

        return (swap, null);
    }

    /// <summary>
    /// Attempts to auto-pay a swap from the store's wallet (Lightning or onchain).
    /// Updates the swap record in DB.
    /// </summary>
    public async Task<(bool paid, string error)> TryAutoPayAsync(
        StoreData store, SwapRecord swap, CancellationToken ct)
    {
        if (swap.Status != SwapStatus.PendingPayment || string.IsNullOrEmpty(swap.PaymentAddress))
            return (false, "Swap is not in a payable state.");

        await using var db = dbContextFactory.CreateContext();
        db.SwapRecords.Attach(swap);

        try
        {
            if (swap.SwapType == SwapType.LightningToUsdc)
            {
                var (success, error) = await PayLightningInvoice(store, swap.PaymentAddress, ct);
                if (success)
                {
                    swap.Status = SwapStatus.PendingClaim;
                    swap.UpdatedAt = DateTimeOffset.UtcNow;
                    logger.LogInformation("Swap {SwapId} auto-paid via Lightning", swap.Id);
                }
                else
                {
                    swap.ErrorMessage = $"Auto-pay failed: {error}. You can pay manually.";
                    logger.LogWarning("Auto-pay failed for swap {SwapId}: {Error}", swap.Id, error);
                }
            }
            else if (swap.SwapType == SwapType.BitcoinToArkade)
            {
                var (success, error, txId) = await PayOnchain(store, swap.PaymentAddress, swap.AmountSats, ct);
                if (success)
                {
                    swap.Status = SwapStatus.PayingFromWallet;
                    swap.TxId = txId;
                    swap.UpdatedAt = DateTimeOffset.UtcNow;
                    logger.LogInformation("Swap {SwapId} auto-paid onchain, TxId: {TxId}", swap.Id, txId);
                }
                else
                {
                    swap.ErrorMessage = $"Auto-pay failed: {error}. You can pay manually.";
                    logger.LogWarning("Auto-pay failed for swap {SwapId}: {Error}", swap.Id, error);
                }
            }
        }
        catch (Exception ex)
        {
            swap.ErrorMessage = $"Auto-pay failed: {ex.Message}. You can pay manually.";
            logger.LogWarning(ex, "Auto-pay exception for swap {SwapId}", swap.Id);
        }

        await db.SaveChangesAsync(ct);

        var paid = swap.Status is SwapStatus.PendingClaim or SwapStatus.PayingFromWallet;
        return (paid, paid ? null : swap.ErrorMessage);
    }

    public async Task<(bool success, string error)> PayLightningInvoice(
        StoreData store, string bolt11, CancellationToken ct)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var pmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmId, handlers);

        if (lnConfig == null)
            return (false, "Lightning is not configured for this store.");

        if (string.IsNullOrEmpty(bolt11) ||
            !BOLT11PaymentRequest.TryParse(bolt11, out var parsedBolt11, network.NBitcoinNetwork))
            return (false, "Invalid BOLT11 invoice format.");

        if (parsedBolt11.ExpiryDate < DateTimeOffset.UtcNow)
            return (false, "BOLT11 invoice has expired.");

        ILightningClient client;
        if (lnConfig.GetExternalLightningUrl() is { } connStr)
            client = lightningClientFactory.Create(connStr, network);
        else if (lnConfig.IsInternalNode &&
                 lightningNetworkOptions.Value.InternalLightningByCryptoCode
                     .TryGetValue("BTC", out var internalNode))
            client = internalNode;
        else
            return (false, "No Lightning node available.");

        PayResponse result;
        try
        {
            result = await client.Pay(bolt11, null, ct);
        }
        catch (Exception ex)
        {
            return (false, $"Lightning payment error: {ex.Message}");
        }

        switch (result.Result)
        {
            case PayResult.Ok:
                return (true, null);
            case PayResult.CouldNotFindRoute:
                return (false, "Could not find route to pay invoice.");
            case PayResult.Unknown:
                if (lnConfig.GetExternalLightningUrl() is { } connStr2)
                    client = lightningClientFactory.Create(connStr2, network);
                else if (lnConfig.IsInternalNode &&
                         lightningNetworkOptions.Value.InternalLightningByCryptoCode
                             .TryGetValue("BTC", out var internalNode2))
                    client = internalNode2;

                try
                {
                    var payment = await client.GetPayment(
                        parsedBolt11.PaymentHash.ToString(), ct);
                    return payment?.Status switch
                    {
                        LightningPaymentStatus.Complete => (true, null),
                        LightningPaymentStatus.Failed => (false, "Payment failed after submission."),
                        _ => (false, "Payment status unknown. Check Lightning node manually.")
                    };
                }
                catch
                {
                    return (false, "Payment submitted but status could not be verified.");
                }
            default:
                return (false, result.ErrorDetail ?? "Lightning payment failed.");
        }
    }

    public async Task<(bool success, string error, string txId)> PayOnchain(
        StoreData store, string address, long amountSats, CancellationToken ct)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var derivationSettings = store.GetDerivationSchemeSettings(handlers, "BTC");

        if (derivationSettings == null)
            return (false, "Onchain wallet not configured for this store.", null);
        if (!derivationSettings.IsHotWallet)
            return (false, "Store wallet is not a hot wallet. Cannot auto-send.", null);

        BitcoinAddress dest;
        try
        {
            dest = BitcoinAddress.Create(address, network.NBitcoinNetwork);
        }
        catch (Exception)
        {
            return (false, "Invalid Bitcoin destination address.", null);
        }

        var explorerClient = explorerClientProvider.GetExplorerClient("BTC");
        var wallet = walletProvider.GetWallet("BTC");

        var extKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivationSettings.AccountDerivation,
            WellknownMetadataKeys.AccountHDKey, ct);
        if (extKeyStr == null)
            return (false, "Account HD key not found. Is this a hot wallet?", null);

        var accountKey = ExtKey.Parse(extKeyStr, network.NBitcoinNetwork);

        var recCoins = (await wallet.GetUnspentCoins(derivationSettings.AccountDerivation,
            cancellation: ct)).ToArray();
        var coins = recCoins.Select(c => c.Coin).ToArray();
        var keys = recCoins.Select(c => accountKey.Derive(c.KeyPath).PrivateKey).ToArray();

        var feeRate = await feeProviderFactory.CreateFeeProvider(network).GetFeeRateAsync(1);

        var changeAddress = await explorerClient.GetUnusedAsync(
            derivationSettings.AccountDerivation, DerivationFeature.Change, 0, true, ct);

        Transaction tx;
        try
        {
            var txBuilder = network.NBitcoinNetwork.CreateTransactionBuilder()
                .AddCoins(coins)
                .AddKeys(keys)
                .Send(dest, Money.Satoshis(amountSats))
                .SetChange(changeAddress.Address)
                .SendEstimatedFees(feeRate);

            tx = txBuilder.BuildTransaction(true);
        }
        catch (NotEnoughFundsException)
        {
            return (false, "Not enough funds in the hot wallet.", null);
        }

        var broadcastResult = await explorerClient.BroadcastAsync(tx, ct);

        if (!broadcastResult.Success)
            return (false, $"Broadcast failed: {broadcastResult.RPCMessage}", null);

        wallet.InvalidateCache(derivationSettings.AccountDerivation);

        return (true, null, tx.GetHash().ToString());
    }

    private static SwapType? MapToSwapType(string source, string target)
    {
        return (source, target) switch
        {
            ("btc_lightning", "usdc_pol") => SwapType.LightningToUsdc,
            ("btc_onchain", "btc_arkade") => SwapType.BitcoinToArkade,
            _ => null
        };
    }
}
