using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Secp256k1;
using NBXplorer;
using Nethereum.Util;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

/// <summary>
/// Handles gasless EVM HTLC claims for BTC→EVM swaps.
/// Uses NBitcoin.Secp256k1 for ECDSA signing (Nethereum's signer has BouncyCastle conflicts).
/// Nethereum.Util is used only for Keccak-256 hashing.
/// </summary>
public class EvmGaslessClaimService(
    BTCPayNetworkProvider networkProvider,
    ExplorerClientProvider explorerClientProvider,
    PaymentMethodHandlerDictionary handlers,
    LendaSwapApiClient apiClient,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<EvmGaslessClaimService> logger)
{
    private IDataProtector Protector =>
        dataProtectionProvider.CreateProtector("LendaSwap.Preimage");

    /// <summary>
    /// Derives an Ethereum address and private key bytes from the store's BTC wallet seed.
    /// </summary>
    public async Task<(byte[] privateKey, string evmAddress)?> DeriveEvmKey(
        StoreData store, CancellationToken ct)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var derivation = store.GetDerivationSchemeSettings(handlers, "BTC");
        if (derivation == null)
            return null;

        var explorerClient = explorerClientProvider.GetExplorerClient("BTC");
        var extKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivation.AccountDerivation, WellknownMetadataKeys.AccountHDKey, ct);
        if (extKeyStr == null)
            return null;

        var accountKey = ExtKey.Parse(extKeyStr, network.NBitcoinNetwork);
        var evmDerivedKey = accountKey.Derive(new KeyPath("0/0"));
        var privateKeyBytes = evmDerivedKey.PrivateKey.ToBytes();

        // Compute Ethereum address: keccak256(uncompressed_pubkey[1:])[12:]
        var ecPriv = ECPrivKey.Create(privateKeyBytes);
        var pubKey = ecPriv.CreatePubKey();
        var pubBytes = new byte[65];
        pubKey.WriteToSpan(false, pubBytes, out _);

        var sha3 = new Sha3Keccack();
        var addrHash = sha3.CalculateHash(pubBytes.AsSpan().Slice(1).ToArray());
        var evmAddress = "0x" + Convert.ToHexString(addrHash.AsSpan().Slice(12)).ToLower();

        return (privateKeyBytes, evmAddress);
    }

    /// <summary>
    /// Gets the store's derived EVM address (for use as claiming_address in swap creation).
    /// </summary>
    public async Task<string> GetClaimingAddress(StoreData store, CancellationToken ct)
    {
        var result = await DeriveEvmKey(store, ct);
        return result?.evmAddress;
    }

    /// <summary>
    /// Claims an EVM HTLC gaslessly by signing an EIP-712 Redeem message.
    /// </summary>
    public async Task<(bool success, string txHash, string error)> TryGaslessClaim(
        StoreData store, SwapRecord swap, GetSwapResponse remote, CancellationToken ct)
    {
        if (swap.SwapType is not (SwapType.LightningToEvm or SwapType.LightningToUsdc or SwapType.BitcoinToEvm))
            return (false, null, "Not a BTC→EVM swap.");

        if (string.IsNullOrEmpty(remote.EvmHtlcAddress) ||
            !remote.EvmChainId.HasValue ||
            !remote.EvmRefundLocktime.HasValue ||
            !remote.EvmExpectedSats.HasValue ||
            string.IsNullOrEmpty(remote.ServerEvmAddress) ||
            string.IsNullOrEmpty(remote.EvmCoordinatorAddress) ||
            string.IsNullOrEmpty(remote.WbtcAddress))
        {
            return (false, null, "EVM HTLC details not yet available.");
        }

        string preimageHex;
        try
        {
            preimageHex = Protector.Unprotect(swap.PreimageEncrypted);
        }
        catch
        {
            return (false, null, "Failed to decrypt preimage.");
        }

        var keyResult = await DeriveEvmKey(store, ct);
        if (keyResult == null)
            return (false, null, "Could not derive EVM key from store wallet.");

        var (privateKeyBytes, claimingAddress) = keyResult.Value;

        if (!string.IsNullOrEmpty(remote.ClientEvmAddress) &&
            !string.Equals(claimingAddress, remote.ClientEvmAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"Claiming address mismatch: ours={claimingAddress}, expected={remote.ClientEvmAddress}");
        }

        var destination = swap.ClaimDestination;
        RedeemAndSwapCalldataResponse calldata;
        try
        {
            calldata = await apiClient.GetRedeemAndSwapCalldata(swap.LendaSwapId, destination, ct);
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to fetch calldata: {ex.Message}");
        }

        var wbtcAddress = remote.WbtcAddress;
        var sweepToken = wbtcAddress;
        var minAmountOut = BigInteger.Zero;

        if (calldata.DexCalldata != null && !string.IsNullOrEmpty(swap.TargetToken))
        {
            sweepToken = swap.TargetToken;
        }

        var preimageBytes = Convert.FromHexString(preimageHex);
        var preimage32 = new byte[32];
        Array.Copy(preimageBytes, preimage32, Math.Min(preimageBytes.Length, 32));

        var chainId = remote.EvmChainId.Value;
        var amount = new BigInteger(remote.EvmExpectedSats.Value);
        var timelock = new BigInteger(remote.EvmRefundLocktime.Value);

        var callsHashHex = calldata.CallsHash;
        if (callsHashHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            callsHashHex = callsHashHex[2..];
        var callsHashBytes = Convert.FromHexString(callsHashHex);

        var (v, r, s) = SignRedeemEip712(
            privateKeyBytes,
            chainId: chainId,
            verifyingContract: remote.EvmHtlcAddress,
            preimage: preimage32,
            amount: amount,
            token: wbtcAddress,
            sender: remote.ServerEvmAddress,
            timelock: timelock,
            caller: remote.EvmCoordinatorAddress,
            destination: destination,
            sweepToken: sweepToken,
            minAmountOut: minAmountOut,
            callsHash: callsHashBytes
        );

        try
        {
            var request = new ClaimGaslessRequest
            {
                Secret = "0x" + preimageHex,
                Destination = destination,
                V = v,
                R = "0x" + Convert.ToHexString(r).ToLowerInvariant(),
                S = "0x" + Convert.ToHexString(s).ToLowerInvariant(),
                DexCalldata = calldata.DexCalldata
            };

            var result = await apiClient.ClaimGasless(swap.LendaSwapId, request, ct);

            logger.LogInformation("Gasless EVM claim succeeded for swap {SwapId}, TxHash: {TxHash}",
                swap.Id, result.TxHash);
            return (true, result.TxHash, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"claim-gasless API call failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Signs the EIP-712 Redeem struct using NBitcoin.Secp256k1.
    /// </summary>
    private static (int v, byte[] r, byte[] s) SignRedeemEip712(
        byte[] privateKey,
        long chainId,
        string verifyingContract,
        byte[] preimage,
        BigInteger amount,
        string token,
        string sender,
        BigInteger timelock,
        string caller,
        string destination,
        string sweepToken,
        BigInteger minAmountOut,
        byte[] callsHash)
    {
        var sha3 = new Sha3Keccack();

        // EIP-712 Domain Separator
        var domainTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));
        var nameHash = sha3.CalculateHash(System.Text.Encoding.UTF8.GetBytes("HTLCErc20"));
        var versionHash = sha3.CalculateHash(System.Text.Encoding.UTF8.GetBytes("3"));

        var domainData = new byte[32 * 5];
        Array.Copy(domainTypeHash, 0, domainData, 0, 32);
        Array.Copy(nameHash, 0, domainData, 32, 32);
        Array.Copy(versionHash, 0, domainData, 64, 32);
        AbiEncodeUint256(new BigInteger(chainId)).CopyTo(domainData, 96);
        AbiEncodeAddress(verifyingContract).CopyTo(domainData, 128);
        var domainSeparator = sha3.CalculateHash(domainData);

        // Redeem struct type hash
        var redeemTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes(
                "Redeem(bytes32 preimage,uint256 amount,address token,address sender,uint256 timelock,address caller,address destination,address sweepToken,uint256 minAmountOut,bytes32 callsHash)"));

        var structData = new byte[32 * 11];
        Array.Copy(redeemTypeHash, 0, structData, 0, 32);
        Array.Copy(PadTo32(preimage), 0, structData, 32, 32);
        AbiEncodeUint256(amount).CopyTo(structData, 64);
        AbiEncodeAddress(token).CopyTo(structData, 96);
        AbiEncodeAddress(sender).CopyTo(structData, 128);
        AbiEncodeUint256(timelock).CopyTo(structData, 160);
        AbiEncodeAddress(caller).CopyTo(structData, 192);
        AbiEncodeAddress(destination).CopyTo(structData, 224);
        AbiEncodeAddress(sweepToken).CopyTo(structData, 256);
        AbiEncodeUint256(minAmountOut).CopyTo(structData, 288);
        Array.Copy(PadTo32(callsHash), 0, structData, 320, 32);
        var structHash = sha3.CalculateHash(structData);

        // Final EIP-712 digest
        var digestInput = new byte[2 + 32 + 32];
        digestInput[0] = 0x19;
        digestInput[1] = 0x01;
        Array.Copy(domainSeparator, 0, digestInput, 2, 32);
        Array.Copy(structHash, 0, digestInput, 34, 32);
        var digest = sha3.CalculateHash(digestInput);

        // Sign with NBitcoin.Secp256k1 (works, unlike Nethereum's broken BouncyCastle signer)
        var ecPrivKey = ECPrivKey.Create(privateKey);
        ecPrivKey.TrySignRecoverable(digest, out var recSig);

        Scalar rScalar = default, sScalar = default;
        int recId = 0;
        recSig.Deconstruct(out rScalar, out sScalar, out recId);

        var rBytes = new byte[32];
        var sBytes = new byte[32];
        rScalar.WriteToSpan(rBytes);
        sScalar.WriteToSpan(sBytes);

        var v = 27 + recId;
        return (v, rBytes, sBytes);
    }

    private static byte[] AbiEncodeUint256(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[32];
        Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
        return padded;
    }

    private static byte[] AbiEncodeAddress(string address)
    {
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            address = address[2..];
        var addressBytes = Convert.FromHexString(address);
        var padded = new byte[32];
        Array.Copy(addressBytes, 0, padded, 32 - addressBytes.Length, addressBytes.Length);
        return padded;
    }

    private static byte[] PadTo32(byte[] data)
    {
        if (data.Length >= 32) return data[..32];
        var padded = new byte[32];
        Array.Copy(data, padded, data.Length);
        return padded;
    }
}
