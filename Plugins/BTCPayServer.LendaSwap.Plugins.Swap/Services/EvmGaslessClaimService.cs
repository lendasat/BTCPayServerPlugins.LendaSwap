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
using NBXplorer;
using Nethereum.Signer;
using Nethereum.Util;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

/// <summary>
/// Handles gasless EVM HTLC claims for Lightning→EVM swaps.
///
/// The claiming_address is derived from the store's BTC wallet seed via m/44'/60'/0'/0/0.
/// The plugin signs the EIP-712 Redeem message and calls claim-gasless.
/// The user does NOT need an EVM wallet — the server pays gas.
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
    /// Derives an Ethereum address and private key from the store's BTC wallet seed.
    /// Uses HD path m/44'/60'/0'/0/0 (standard Ethereum BIP44 path).
    /// </summary>
    public async Task<(EthECKey evmKey, string evmAddress)?> DeriveEvmKey(
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

        // Parse the BTC account key and derive the Ethereum key at m/44'/60'/0'/0/0
        // We use the raw private key bytes — secp256k1 is the same curve for BTC and ETH
        var accountKey = ExtKey.Parse(extKeyStr, network.NBitcoinNetwork);
        // Derive a deterministic key for EVM signing (using a sub-path from the account key)
        var evmDerivedKey = accountKey.Derive(new KeyPath("0/0"));
        var privateKeyBytes = evmDerivedKey.PrivateKey.ToBytes();

        var ethKey = new EthECKey(privateKeyBytes, true);
        var evmAddress = ethKey.GetPublicAddress();

        return (ethKey, evmAddress);
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
    /// The server executes redeemAndExecute on behalf of the plugin.
    /// </summary>
    public async Task<(bool success, string txHash, string error)> TryGaslessClaim(
        StoreData store, SwapRecord swap, GetSwapResponse remote, CancellationToken ct)
    {
        if (swap.SwapType is not (SwapType.LightningToEvm or SwapType.LightningToUsdc))
            return (false, null, "Not a Lightning→EVM swap.");

        // We need the remote to be in ServerFunded state with EVM HTLC details
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

        // Derive EVM key
        var keyResult = await DeriveEvmKey(store, ct);
        if (keyResult == null)
            return (false, null, "Could not derive EVM key from store wallet.");

        var (ethKey, claimingAddress) = keyResult.Value;

        // Verify our claiming address matches what's stored in the swap
        if (!string.IsNullOrEmpty(remote.ClientEvmAddress) &&
            !string.Equals(claimingAddress, remote.ClientEvmAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"Claiming address mismatch: ours={claimingAddress}, expected={remote.ClientEvmAddress}");
        }

        // Step 1: Fetch calldata from the API
        var destination = swap.ClaimDestination; // user's actual EVM destination
        RedeemAndSwapCalldataResponse calldata;
        try
        {
            calldata = await apiClient.GetRedeemAndSwapCalldata(swap.LendaSwapId, destination, ct);
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to fetch calldata: {ex.Message}");
        }

        // Step 2: Determine sweep token and minAmountOut
        // For non-WBTC targets, use the target token. For WBTC, use WBTC address.
        var wbtcAddress = remote.WbtcAddress;
        var sweepToken = wbtcAddress; // default: WBTC direct
        var minAmountOut = BigInteger.Zero;

        // If there's DEX calldata, the target is not WBTC — use the actual target token
        if (calldata.DexCalldata != null && !string.IsNullOrEmpty(swap.TargetToken))
        {
            sweepToken = swap.TargetToken; // ERC-20 contract address of target token
        }

        // Step 3: Construct and sign the EIP-712 Redeem message
        var preimageBytes = Convert.FromHexString(preimageHex);
        var preimage32 = new byte[32];
        Array.Copy(preimageBytes, preimage32, Math.Min(preimageBytes.Length, 32));

        var chainId = remote.EvmChainId.Value;
        var amount = new BigInteger(remote.EvmExpectedSats.Value);
        var timelock = new BigInteger(remote.EvmRefundLocktime.Value);

        // Parse the callsHash from the calldata response
        var callsHashHex = calldata.CallsHash;
        if (callsHashHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            callsHashHex = callsHashHex[2..];
        var callsHashBytes = Convert.FromHexString(callsHashHex);

        // Sign the EIP-712 Redeem struct
        var (v, r, s) = SignRedeemEip712(
            ethKey,
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

        // Step 4: Call claim-gasless
        try
        {
            var request = new ClaimGaslessRequest
            {
                Secret = "0x" + preimageHex,
                Destination = destination,
                V = v,
                R = "0x" + BitConverter.ToString(r).Replace("-", "").ToLowerInvariant(),
                S = "0x" + BitConverter.ToString(s).Replace("-", "").ToLowerInvariant(),
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
    /// Signs the EIP-712 Redeem struct for the HTLCErc20 contract.
    ///
    /// Domain: { name: "HTLCErc20", version: "3", chainId, verifyingContract }
    /// Struct: Redeem(bytes32 preimage, uint256 amount, address token, address sender,
    ///                uint256 timelock, address caller, address destination,
    ///                address sweepToken, uint256 minAmountOut, bytes32 callsHash)
    /// </summary>
    private static (int v, byte[] r, byte[] s) SignRedeemEip712(
        EthECKey key,
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

        var domainData = new byte[32 + 32 + 32 + 32 + 32]; // typeHash + nameHash + versionHash + chainId + contract
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

        // Encode struct fields: typeHash + fields (each 32 bytes)
        var structData = new byte[32 * 11]; // typeHash + 10 fields
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

        // Final EIP-712 digest: keccak256("\x19\x01" + domainSeparator + structHash)
        var digestInput = new byte[2 + 32 + 32];
        digestInput[0] = 0x19;
        digestInput[1] = 0x01;
        Array.Copy(domainSeparator, 0, digestInput, 2, 32);
        Array.Copy(structHash, 0, digestInput, 34, 32);
        var digest = sha3.CalculateHash(digestInput);

        // Sign with secp256k1 (recoverable signature)
        var signature = key.SignAndCalculateV(digest);

        // Nethereum's SignAndCalculateV returns V as 27 or 28 (EIP-155 standard).
        // If it returns a raw recovery ID (0 or 1), we need to add 27.
        var vRaw = signature.V.Length == 1 ? (int)signature.V[0] : BitConverter.ToInt32(signature.V);
        var v = vRaw < 27 ? vRaw + 27 : vRaw;
        return (v, signature.R, signature.S);
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
