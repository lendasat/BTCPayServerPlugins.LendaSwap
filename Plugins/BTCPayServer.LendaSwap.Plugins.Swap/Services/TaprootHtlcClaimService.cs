using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

/// <summary>
/// Claims BTC from Taproot HTLCs for EVM→Bitcoin swaps.
///
/// Taproot HTLC script structure (from LendaSwap Rust):
///   Claim leaf:  [user_claim_pk] OP_CHECKSIGVERIFY OP_HASH160 [hash_lock_20b] OP_EQUAL
///   Refund leaf: [locktime] OP_CLTV OP_DROP [server_refund_pk] OP_CHECKSIG
///   Internal key: NUMS unspendable point
///   Tree: balanced 2-leaf at depth 1 (claim=left, refund=right)
///
/// Witness for claim: [secret_32b, schnorr_sig_64b, claim_script, control_block]
/// </summary>
public class TaprootHtlcClaimService(
    BTCPayNetworkProvider networkProvider,
    ExplorerClientProvider explorerClientProvider,
    BTCPayWalletProvider walletProvider,
    PaymentMethodHandlerDictionary handlers,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<TaprootHtlcClaimService> logger)
{
    // NUMS unspendable internal key — standard "Nothing Up My Sleeve" point.
    // Same as ark_rs::core::UNSPENDABLE_KEY. All spends must go through script paths.
    // Compressed pubkey 0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0
    // x-only (32 bytes): 50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0
    private static readonly TaprootInternalPubKey NUMSKey = new(
        Convert.FromHexString("50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0"));

    private IDataProtector Protector =>
        dataProtectionProvider.CreateProtector("LendaSwap.Preimage");

    /// <summary>
    /// Attempts to claim BTC from a Taproot HTLC for an EVM→Bitcoin swap.
    /// Returns (success, txId, error).
    /// </summary>
    public async Task<(bool success, string txId, string error)> TryClaimBtcHtlc(
        StoreData store, SwapRecord swap, GetSwapResponse remote, CancellationToken ct)
    {
        if (swap.SwapType != SwapType.EvmToBitcoin)
            return (false, null, "Not an EVM→Bitcoin swap.");

        // Validate all required fields from the remote response
        if (string.IsNullOrEmpty(remote.BtcHtlcAddress) ||
            string.IsNullOrEmpty(remote.BtcFundTxid) ||
            !remote.BtcFundVout.HasValue ||
            string.IsNullOrEmpty(remote.BtcUserClaimPk) ||
            string.IsNullOrEmpty(remote.BtcServerRefundPk) ||
            string.IsNullOrEmpty(remote.BtcHashLock) ||
            !remote.BtcRefundLocktime.HasValue)
        {
            return (false, null, "BTC HTLC details not yet available from API.");
        }

        // Decrypt the preimage
        string preimageHex;
        try
        {
            preimageHex = Protector.Unprotect(swap.PreimageEncrypted);
        }
        catch
        {
            return (false, null, "Failed to decrypt preimage.");
        }
        var preimageBytes = Convert.FromHexString(preimageHex);

        // Get the claim private key from the store's hot wallet
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var derivation = store.GetDerivationSchemeSettings(handlers, "BTC");
        if (derivation == null || !derivation.IsHotWallet)
            return (false, null, "Hot wallet not available for BTC claim.");

        var explorerClient = explorerClientProvider.GetExplorerClient("BTC");
        var extKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivation.AccountDerivation, WellknownMetadataKeys.AccountHDKey, ct);
        if (extKeyStr == null)
            return (false, null, "Account HD key not found.");

        var accountKey = ExtKey.Parse(extKeyStr, network.NBitcoinNetwork);
        var claimKey = accountKey.Derive(new KeyPath("0/0")).PrivateKey;

        // Parse remote data
        var userClaimXOnly = new TaprootPubKey(Convert.FromHexString(remote.BtcUserClaimPk));
        var serverRefundXOnly = new TaprootPubKey(Convert.FromHexString(remote.BtcServerRefundPk));
        var btcHashLock = Convert.FromHexString(remote.BtcHashLock); // 20 bytes HASH160
        var refundLocktime = (uint)remote.BtcRefundLocktime.Value;

        // Verify our claim key matches what the API expects
        var ourXOnly = claimKey.PubKey.TaprootInternalKey;
        if (ourXOnly.ToString() != userClaimXOnly.ToString())
        {
            return (false, null, $"Claim key mismatch: our={ourXOnly}, expected={userClaimXOnly}");
        }

        // Build the Taproot HTLC scripts (must match LendaSwap Rust exactly)
        // Claim leaf: <user_claim_pk> OP_CHECKSIGVERIFY OP_HASH160 <hash_lock> OP_EQUAL
        var claimScript = new Script(new Op[]
        {
            Op.GetPushOp(userClaimXOnly.ToBytes()),
            OpcodeType.OP_CHECKSIGVERIFY,
            OpcodeType.OP_HASH160,
            Op.GetPushOp(btcHashLock),
            OpcodeType.OP_EQUAL
        });

        // Refund leaf: <locktime> OP_CLTV OP_DROP <server_refund_pk> OP_CHECKSIG
        var refundScript = new Script(new Op[]
        {
            Op.GetPushOp((long)refundLocktime),
            OpcodeType.OP_CHECKLOCKTIMEVERIFY,
            OpcodeType.OP_DROP,
            Op.GetPushOp(serverRefundXOnly.ToBytes()),
            OpcodeType.OP_CHECKSIG
        });

        // Build Taproot tree: claim=left (depth 1), refund=right (depth 1)
        var tapLeafClaim = new TapScript(claimScript, TapLeafVersion.C0);
        var tapLeafRefund = new TapScript(refundScript, TapLeafVersion.C0);

        var builder = new TaprootBuilder();
        builder.AddLeaf(1, tapLeafClaim);
        builder.AddLeaf(1, tapLeafRefund);
        var spendInfo = builder.Finalize(NUMSKey);

        // Verify the computed address matches the API's address
        var computedAddress = spendInfo.OutputPubKey.OutputKey.GetAddress(network.NBitcoinNetwork);
        var remoteAddress = BitcoinAddress.Create(remote.BtcHtlcAddress, network.NBitcoinNetwork);
        if (computedAddress.ToString() != remoteAddress.ToString())
        {
            logger.LogWarning("HTLC address mismatch: computed={Computed}, remote={Remote}",
                computedAddress, remoteAddress);
            return (false, null, $"HTLC address mismatch: computed={computedAddress}, remote={remoteAddress}");
        }

        logger.LogInformation("HTLC address verified for swap {SwapId}: {Address}", swap.Id, computedAddress);

        // Parse the funding UTXO
        var fundTxId = uint256.Parse(remote.BtcFundTxid);
        var fundVout = remote.BtcFundVout.Value;
        var targetAmountSats = long.TryParse(remote.TargetAmount, out var amt) ? amt : swap.AmountSats;

        // Get a destination address from the store wallet to sweep BTC into
        var destKeyInfo = await explorerClient.GetUnusedAsync(
            derivation.AccountDerivation, DerivationFeature.Deposit, 0, true, ct);
        var destAddress = destKeyInfo.Address;

        // Estimate fee (Taproot script-path spend ≈ 150 vbytes)
        FeeRate feeRate;
        try
        {
            var feeRateResult = await explorerClient.GetFeeRateAsync(6, ct);
            feeRate = feeRateResult.FeeRate;
        }
        catch
        {
            feeRate = new FeeRate(Money.Satoshis(2), 1); // fallback 2 sat/vb
        }
        var estimatedFee = feeRate.GetFee(150);
        var outputAmount = Money.Satoshis(targetAmountSats) - estimatedFee;

        if (outputAmount.Satoshi <= 546)
            return (false, null, $"Output amount after fees ({outputAmount.Satoshi} sats) is below dust limit.");

        // Build the claim transaction
        var tx = Transaction.Create(network.NBitcoinNetwork);
        tx.Version = 2;
        tx.LockTime = LockTime.Zero; // no locktime for claim path

        var txIn = new TxIn(new OutPoint(fundTxId, fundVout))
        {
            Sequence = Sequence.OptInRBF
        };
        tx.Inputs.Add(txIn);
        tx.Outputs.Add(new TxOut(outputAmount, destAddress));

        // Sign with Taproot script-path spend
        var spentOutput = new TxOut(Money.Satoshis(targetAmountSats),
            spendInfo.OutputPubKey.ScriptPubKey);

        // TaprootExecutionData for script-path: input index 0, with tapleaf hash
        var tapleafHash = tapLeafClaim.LeafHash;
        var execData = new TaprootExecutionData(0, tapleafHash)
        {
            SigHash = TaprootSigHash.Default
        };

        var sigHash = tx.GetSignatureHashTaproot(new[] { spentOutput }, execData);

        // SignTaprootScriptSpend for script-path (not KeySpend)
        var schnorrSig = claimKey.SignTaprootScriptSpend(sigHash, spendInfo.MerkleRoot, TaprootSigHash.Default);

        // Build witness: [secret, signature, claim_script, control_block]
        var controlBlock = spendInfo.GetControlBlock(tapLeafClaim);
        tx.Inputs[0].WitScript = new WitScript(new[]
        {
            preimageBytes,
            schnorrSig.ToBytes(),
            claimScript.ToBytes(),
            controlBlock.ToBytes()
        });

        // Broadcast
        var result = await explorerClient.BroadcastAsync(tx, ct);
        if (!result.Success)
            return (false, null, $"Broadcast failed: {result.RPCMessage}");

        var txIdStr = tx.GetHash().ToString();
        logger.LogInformation("BTC HTLC claimed for swap {SwapId}, TxId: {TxId}, amount: {Amount} sats",
            swap.Id, txIdStr, outputAmount.Satoshi);

        // Invalidate wallet cache so the balance updates
        walletProvider.GetWallet("BTC").InvalidateCache(derivation.AccountDerivation);

        return (true, txIdStr, null);
    }
}
