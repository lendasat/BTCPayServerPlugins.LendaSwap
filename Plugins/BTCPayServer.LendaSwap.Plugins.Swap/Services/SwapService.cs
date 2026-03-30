using System;
using System.Collections.Generic;
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
    EvmGaslessClaimService evmClaimService,
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
        var swapType = MapToSwapType(model.SourceChain, model.TargetChain);
        logger.LogInformation("CreateSwap: SourceChain={Src}, TargetChain={Tgt}, SourceToken={ST}, TargetToken={TT}, SwapType={Type}",
            model.SourceChain, model.TargetChain, model.SourceToken, model.TargetToken, swapType);
        if (swapType is null)
            return (null, $"Unsupported swap pair selected. (source={model.SourceChain}, target={model.TargetChain})");

        var (pubKeyHex, isEphemeral) = await GetSwapPubKeyHex(store, ct);
        if (isEphemeral)
            return (null, "A Bitcoin wallet must be configured for this store to create swaps. Configure an onchain hot wallet in your store settings.");

        // Validate destination address format
        // For EVM→BTC (receive) swaps, the deposit address is derived from the store wallet (gasless mode)
        var isEvmToBtcSwap = IsEvmChain(model.SourceChain) && !IsEvmChain(model.TargetChain);
        if (!isEvmToBtcSwap)
        {
            if (string.IsNullOrWhiteSpace(model.ClaimDestination))
                return (null, "Destination address is required.");

            if (IsEvmChain(model.TargetChain))
            {
                var addr = model.ClaimDestination.Trim();
                if (!addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || addr.Length != 42)
                    return (null, "Invalid EVM address format. Expected 0x followed by 40 hex characters.");
            }
        }

        var preimage = cryptoHelper.GeneratePreimage();
        var preimageHex = Convert.ToHexString(preimage).ToLowerInvariant();

        logger.LogInformation("Creating {SwapType} swap for store {StoreId}, amount {Amount} sats",
            swapType, storeId, model.AmountSats);

        var claimDest = isEvmToBtcSwap ? null : model.ClaimDestination;

        var swap = new SwapRecord
        {
            StoreId = storeId,
            SwapType = swapType.Value,
            Status = SwapStatus.Created,
            AmountSats = model.AmountSats,
            ClaimDestination = claimDest,
            SourceToken = model.SourceToken,
            TargetToken = model.TargetToken,
            SourceChain = model.SourceChain,
            TargetChain = model.TargetChain,
            PreimageEncrypted = Protector.Protect(preimageHex),
            RefundPubKeyHex = pubKeyHex,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            switch (swapType.Value)
            {
                case SwapType.LightningToEvm:
                    await CreateBtcToEvmSwap(swap, model, store, pubKeyHex, preimage, isLightning: true, ct);
                    break;
                case SwapType.BitcoinToEvm:
                    await CreateBtcToEvmSwap(swap, model, store, pubKeyHex, preimage, isLightning: false, ct);
                    break;
                case SwapType.BitcoinToArkade:
                    await CreateBtcToArkadeSwap(swap, model, pubKeyHex, preimage, isLightning: false, ct);
                    break;
                case SwapType.LightningToArkade:
                    await CreateBtcToArkadeSwap(swap, model, pubKeyHex, preimage, isLightning: true, ct);
                    break;
                case SwapType.EvmToLightning:
                    await CreateEvmToLightningSwap(swap, model, store, pubKeyHex, ct);
                    break;
                case SwapType.EvmToBitcoin:
                    await CreateEvmToBitcoinSwap(swap, model, store, pubKeyHex, preimage, ct);
                    break;
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
    /// BTC (Lightning or onchain) → EVM swap: compute SHA256 hash, derive EVM claiming key, call API.
    /// </summary>
    private async Task CreateBtcToEvmSwap(
        SwapRecord swap, CreateSwapViewModel model, StoreData store, string pubKeyHex,
        byte[] preimage, bool isLightning, CancellationToken ct)
    {
        var paymentHash = cryptoHelper.ComputePaymentHash(preimage);
        swap.PaymentHash = paymentHash;

        var claimingAddress = await evmClaimService.GetClaimingAddress(store, ct);
        if (string.IsNullOrEmpty(claimingAddress))
            throw new InvalidOperationException("Could not derive EVM claiming key from store wallet.");

        var request = new LightningToEvmSwapRequest
        {
            HashLock = "0x" + paymentHash,
            AmountIn = model.AmountSats,
            ClaimingAddress = claimingAddress,
            TargetAddress = model.ClaimDestination,
            TokenAddress = model.TargetToken,
            EvmChainId = ParseEvmChainId(model.TargetChain),
            Gasless = true,
            UserId = pubKeyHex,
            RefundPk = pubKeyHex
        };

        if (isLightning)
        {
            var result = await apiClient.CreateLightningToEvmSwap(request, ct);
            swap.LendaSwapId = result.Id;
            swap.PaymentAddress = result.Bolt11Invoice;
            swap.TargetHtlcAddress = result.EvmHtlcAddress;
            swap.HtlcExpiryBlock = result.EvmRefundLocktime;
            swap.AmountSats = long.TryParse(result.SourceAmount, out var srcAmt) ? srcAmt : model.AmountSats;
        }
        else
        {
            var result = await apiClient.CreateBitcoinToEvmSwap(request, ct);
            swap.LendaSwapId = result.Id;
            swap.PaymentAddress = result.BtcHtlcAddress;
            swap.TargetHtlcAddress = result.EvmHtlcAddress;
            swap.HtlcExpiryBlock = result.BtcRefundLocktime;
            swap.AmountSats = long.TryParse(result.SourceAmount, out var srcAmt) ? srcAmt : model.AmountSats;
        }

        swap.Status = SwapStatus.PendingPayment;
    }

    /// <summary>
    /// BTC (Lightning or onchain) → Arkade swap.
    /// </summary>
    private async Task CreateBtcToArkadeSwap(
        SwapRecord swap, CreateSwapViewModel model, string pubKeyHex,
        byte[] preimage, bool isLightning, CancellationToken ct)
    {
        if (isLightning)
        {
            var paymentHash = cryptoHelper.ComputePaymentHash(preimage);
            swap.PaymentHash = paymentHash;

            var result = await apiClient.CreateLightningToArkadeSwap(new LightningToArkadeSwapRequest
            {
                HashLock = "0x" + paymentHash,
                SatsReceive = model.AmountSats,
                TargetArkadeAddress = model.ClaimDestination,
                ClaimPk = pubKeyHex,
                UserId = pubKeyHex
            }, ct);

            swap.LendaSwapId = result.Id;
            swap.PaymentAddress = result.Bolt11Invoice;
            swap.TargetHtlcAddress = result.ArkadeVhtlcAddress;
            swap.HtlcExpiryBlock = result.VhtlcRefundLocktime;
            swap.AmountSats = long.TryParse(result.SourceAmount, out var srcAmt) ? srcAmt : model.AmountSats;
        }
        else
        {
            var hash160 = cryptoHelper.ComputeHash160(preimage);
            swap.PaymentHash = hash160;

            var result = await apiClient.CreateBitcoinToArkadeSwap(new BitcoinToArkadeSwapRequest
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
            swap.AmountSats = long.TryParse(result.SourceAmount, out var srcAmt) ? srcAmt : model.AmountSats;
        }

        swap.Status = SwapStatus.PendingPayment;
    }

    /// <summary>
    /// EVM → Lightning swap: generate LN invoice, server pays it after user funds EVM HTLC.
    /// </summary>
    private async Task CreateEvmToLightningSwap(
        SwapRecord swap, CreateSwapViewModel model, StoreData store, string pubKeyHex,
        CancellationToken ct)
    {
        var invoice = await CreateLightningInvoice(store, model.AmountSats, "LendaSwap: receive BTC", ct);
        if (invoice.bolt11 == null)
        {
            swap.Status = SwapStatus.Failed;
            swap.ErrorMessage = invoice.error;
            return;
        }

        // Derive a deterministic EVM address from the store's wallet seed.
        // In gasless mode, this becomes the deposit address where the sender sends tokens.
        // The plugin holds the private key and can sign Permit2 to fund the HTLC.
        var evmKey = await evmClaimService.DeriveEvmKey(store, ct);
        if (evmKey == null)
        {
            swap.Status = SwapStatus.Failed;
            swap.ErrorMessage = "Could not derive EVM key from store wallet. Ensure a hot wallet is configured.";
            return;
        }

        var result = await apiClient.CreateEvmToLightningSwap(new EvmToLightningSwapRequest
        {
            LightningInvoice = invoice.bolt11,
            EvmChainId = ParseEvmChainId(model.SourceChain),
            TokenAddress = model.SourceToken,
            UserAddress = evmKey.Value.evmAddress,
            UserId = pubKeyHex,
            Gasless = true
        }, ct);

        swap.LendaSwapId = result.Id;
        swap.PaymentHash = result.HashLock;
        swap.EvmHtlcAddress = result.EvmHtlcAddress;
        swap.EvmCoordinatorAddress = result.EvmCoordinatorAddress;
        swap.EvmDepositAddress = result.ClientEvmAddress;
        swap.EvmGasless = true;
        swap.SourceAmountRaw = result.SourceAmount;
        swap.TargetHtlcAddress = result.ClientLightningInvoice;
        swap.ClaimDestination = "Lightning (" + invoice.bolt11[..20] + "...)";
        swap.HtlcExpiryBlock = result.EvmRefundLocktime;
        swap.AmountSats = long.TryParse(result.TargetAmount, out var tgtAmt) ? tgtAmt : model.AmountSats;
        swap.Status = SwapStatus.PendingPayment;
    }

    /// <summary>
    /// EVM → Bitcoin swap: plugin generates preimage + hash_lock, provides claim_pk.
    /// After user funds EVM HTLC, server sends BTC to a Taproot HTLC that the plugin can claim.
    /// </summary>
    private async Task CreateEvmToBitcoinSwap(
        SwapRecord swap, CreateSwapViewModel model, StoreData store, string pubKeyHex,
        byte[] preimage, CancellationToken ct)
    {
        var paymentHash = cryptoHelper.ComputePaymentHash(preimage);
        swap.PaymentHash = paymentHash;

        var evmKey = await evmClaimService.DeriveEvmKey(store, ct);
        if (evmKey == null)
        {
            swap.Status = SwapStatus.Failed;
            swap.ErrorMessage = "Could not derive EVM key from store wallet. Ensure a hot wallet is configured.";
            return;
        }

        var result = await apiClient.CreateEvmToBitcoinSwap(new EvmToBitcoinSwapRequest
        {
            HashLock = "0x" + paymentHash,
            AmountOut = model.AmountSats,
            EvmChainId = ParseEvmChainId(model.SourceChain),
            TokenAddress = model.SourceToken,
            UserAddress = evmKey.Value.evmAddress,
            ClaimPk = pubKeyHex,
            UserId = pubKeyHex,
            Gasless = true
        }, ct);

        swap.LendaSwapId = result.Id;
        swap.EvmHtlcAddress = result.EvmHtlcAddress;
        swap.EvmCoordinatorAddress = result.EvmCoordinatorAddress;
        swap.EvmDepositAddress = result.ClientEvmAddress;
        swap.EvmGasless = true;
        swap.SourceAmountRaw = result.SourceAmount;
        swap.TargetHtlcAddress = result.BtcHtlcAddress;
        swap.ClaimDestination = result.BtcHtlcAddress;
        swap.HtlcExpiryBlock = result.BtcRefundLocktime;
        swap.AmountSats = long.TryParse(result.TargetAmount, out var tgtAmt) ? tgtAmt : model.AmountSats;
        swap.Status = SwapStatus.PendingPayment;
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
            if (swap.SwapType is SwapType.LightningToEvm or SwapType.LightningToArkade)
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
            else if (swap.SwapType is SwapType.BitcoinToArkade or SwapType.BitcoinToEvm)
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
                // Payment was submitted but result is ambiguous — check status
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

        var feeRate = await feeProviderFactory.CreateFeeProvider(network).GetFeeRateAsync(1);

        var changeAddress = await explorerClient.GetUnusedAsync(
            derivationSettings.AccountDerivation, DerivationFeature.Change, 0, true, ct);

        Transaction tx;
        try
        {
            // Build unsigned first for coin selection, then derive only the needed keys
            var txBuilder = network.NBitcoinNetwork.CreateTransactionBuilder()
                .AddCoins(coins)
                .Send(dest, Money.Satoshis(amountSats))
                .SetChange(changeAddress.Address)
                .SendEstimatedFees(feeRate);

            // BuildTransaction(sign: false) performs coin selection without needing keys
            var unsignedTx = txBuilder.BuildTransaction(false);

            // Derive keys only for the selected inputs
            var selectedOutpoints = unsignedTx.Inputs.Select(i => i.PrevOut).ToHashSet();
            var selectedKeys = recCoins
                .Where(c => selectedOutpoints.Contains(c.Coin.Outpoint))
                .Select(c => accountKey.Derive(c.KeyPath).PrivateKey)
                .ToArray();

            txBuilder.AddKeys(selectedKeys);
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

    /// <summary>
    /// Creates a Lightning invoice using the store's configured Lightning node.
    /// Returns (bolt11, error). If bolt11 is null, error describes the failure.
    /// </summary>
    public async Task<(string bolt11, string error)> CreateLightningInvoice(
        StoreData store, long amountSats, string description, CancellationToken ct)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var lnClient = GetLightningClient(store, network);
        if (lnClient == null)
            return (null, "Lightning is not configured for this store. Required for EVM→Lightning swaps.");

        try
        {
            var invoice = await lnClient.CreateInvoice(
                LightMoney.Satoshis(amountSats),
                description,
                TimeSpan.FromHours(24),
                ct);
            return (invoice.BOLT11, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to create Lightning invoice: {ex.Message}");
        }
    }

    public ILightningClient GetLightningClient(StoreData store, BTCPayNetwork network = null)
    {
        network ??= networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var pmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmId, handlers);
        if (lnConfig == null)
            return null;

        if (lnConfig.GetExternalLightningUrl() is { } connStr)
            return lightningClientFactory.Create(connStr, network);

        if (lnConfig.IsInternalNode &&
            lightningNetworkOptions.Value.InternalLightningByCryptoCode
                .TryGetValue("BTC", out var internalNode))
            return internalNode;

        return null;
    }

    // EVM chain IDs as returned by the v2 /tokens API
    private static readonly HashSet<string> EvmChainIds = new(StringComparer.OrdinalIgnoreCase)
        { "137", "1", "42161" };

    // Chain identifiers can be names ("Lightning", "Bitcoin", "Arkade") or numeric IDs ("137", "1", "42161")
    private static long ParseEvmChainId(string chain) => chain?.ToLowerInvariant() switch
    {
        "137" or "polygon" => 137,
        "1" or "ethereum" => 1,
        "42161" or "arbitrum" => 42161,
        _ => long.TryParse(chain, out var id) ? id : throw new ArgumentException($"Unknown EVM chain: {chain}")
    };

    private static bool IsEvmChain(string chain) =>
        EvmChainIds.Contains(chain) ||
        string.Equals(chain, "polygon", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(chain, "ethereum", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(chain, "arbitrum", StringComparison.OrdinalIgnoreCase);

    private static SwapType? MapToSwapType(string sourceChain, string targetChain)
    {
        var src = sourceChain?.ToLowerInvariant();
        var tgt = targetChain?.ToLowerInvariant();

        if (src == null || tgt == null) return null;

        return (src, tgt) switch
        {
            ("lightning", "arkade") => SwapType.LightningToArkade,
            ("bitcoin", "arkade") => SwapType.BitcoinToArkade,
            ("lightning", _) when IsEvmChain(tgt) => SwapType.LightningToEvm,
            ("bitcoin", _) when IsEvmChain(tgt) => SwapType.BitcoinToEvm,
            (_, "lightning") when IsEvmChain(src) => SwapType.EvmToLightning,
            (_, "bitcoin") when IsEvmChain(src) => SwapType.EvmToBitcoin,
            _ => null
        };
    }
}
