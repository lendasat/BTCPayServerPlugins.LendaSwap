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
        if (swap.SwapType is not (SwapType.LightningToEvm or SwapType.BitcoinToEvm))
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

        if (string.IsNullOrEmpty(swap.PreimageEncrypted))
            return (false, null, "Preimage not stored for this swap");

        string preimageHex;
        try
        {
            preimageHex = Protector.Unprotect(swap.PreimageEncrypted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decrypt preimage for swap {SwapId}", swap.Id);
            return (false, null, "Failed to decrypt preimage. DataProtection keys may have changed.");
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
        if (preimageBytes.Length != 32)
            return (false, null, $"Preimage must be exactly 32 bytes, got {preimageBytes.Length}");
        var preimage32 = preimageBytes;

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
    /// Funds a gasless EVM→BTC swap by signing a Permit2 message and calling fund-gasless.
    /// </summary>
    public async Task<(bool success, string txHash, string error)> TryGaslessFund(
        StoreData store, SwapRecord swap, CancellationToken ct)
    {
        var evmKey = await DeriveEvmKey(store, ct);
        if (evmKey == null)
            return (false, null, "Cannot derive EVM key from store wallet");

        var (privateKey, evmAddress) = evmKey.Value;

        try
        {
            // 1. Get Permit2 funding calldata from LendaSwap API
            var calldata = await apiClient.GetPermit2FundingCalldata(swap.LendaSwapId, ct);

            // 2. Generate nonce and deadline
            var nonceBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var nonce = new BigInteger(nonceBytes, isUnsigned: true, isBigEndian: true);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();

            // 3. Parse chain ID from swap's source chain
            if (!long.TryParse(swap.SourceChain, out var chainId))
                return (false, null, $"Cannot parse chain ID from '{swap.SourceChain}'");

            // 4. Sign the Permit2 PermitWitnessTransferFrom EIP-712 digest
            var signature = SignPermit2Funding(
                privateKey, chainId,
                calldata.SourceTokenAddress, (ulong)calldata.SourceAmount,
                calldata.CoordinatorAddress, nonce, deadline,
                calldata.PreimageHash, calldata.LockTokenAddress,
                calldata.ClaimAddress, calldata.CoordinatorAddress,
                calldata.Timelock, calldata.CallsHash);

            // 5. Build EIP-2612 permit if needed (token approval to Permit2)
            Eip2612Permit eip2612Permit = null;
            if (calldata.Eip2612 is { Supported: true, AlreadyApproved: false })
            {
                var permitSig = SignEip2612Permit(
                    privateKey, calldata.Eip2612.DomainSeparator,
                    evmAddress, "0x000000000022D473030F116dDEE9F6B43aC78BA3",
                    calldata.Eip2612.Nonce, deadline);
                eip2612Permit = new Eip2612Permit
                {
                    V = permitSig.v,
                    R = "0x" + Convert.ToHexString(permitSig.r).ToLowerInvariant(),
                    S = "0x" + Convert.ToHexString(permitSig.s).ToLowerInvariant(),
                    Value = (BigInteger.Pow(2, 256) - 1).ToString(), // max uint256
                    Deadline = deadline
                };
            }

            // 6. Call fund-gasless
            var result = await apiClient.FundGasless(swap.LendaSwapId, new FundGaslessRequest
            {
                Permit2Nonce = nonce.ToString(),
                Permit2Deadline = deadline,
                Permit2Signature = signature,
                Calls = calldata.Calls,
                Eip2612Permit = eip2612Permit
            }, ct);

            return (true, result.TxHash, null);
        }
        catch (LendaSwapApiException ex) when (ex.StatusCode == 422 || ex.StatusCode == 400)
        {
            // Likely "no balance" or "swap not ready" — don't retry immediately
            return (false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, null, $"fund-gasless failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Signs the Permit2 PermitWitnessTransferFrom EIP-712 digest for HTLC funding.
    /// </summary>
    private static string SignPermit2Funding(
        byte[] privateKey, long chainId,
        string sourceToken, ulong sourceAmount,
        string coordinatorAddress, BigInteger nonce, long deadline,
        string preimageHash, string lockToken,
        string claimAddress, string refundAddress,
        long timelock, string callsHash)
    {
        var sha3 = new Sha3Keccack();

        // Permit2 domain separator
        var domainTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes("EIP712Domain(string name,uint256 chainId,address verifyingContract)"));
        var nameHash = sha3.CalculateHash(System.Text.Encoding.UTF8.GetBytes("Permit2"));
        const string permit2Address = "0x000000000022D473030F116dDEE9F6B43aC78BA3";

        var domainData = new byte[32 * 4];
        Array.Copy(domainTypeHash, 0, domainData, 0, 32);
        Array.Copy(nameHash, 0, domainData, 32, 32);
        AbiEncodeUint256(new BigInteger(chainId)).CopyTo(domainData, 64);
        AbiEncodeAddress(permit2Address).CopyTo(domainData, 96);
        var domainSeparator = sha3.CalculateHash(domainData);

        // TokenPermissions hash
        var tokenPermTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes("TokenPermissions(address token,uint256 amount)"));
        var tokenPermData = new byte[32 * 3];
        Array.Copy(tokenPermTypeHash, 0, tokenPermData, 0, 32);
        AbiEncodeAddress(sourceToken).CopyTo(tokenPermData, 32);
        AbiEncodeUint256(new BigInteger(sourceAmount)).CopyTo(tokenPermData, 64);
        var tokenPermHash = sha3.CalculateHash(tokenPermData);

        // ExecuteAndCreate witness hash
        var witnessTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes(
                "ExecuteAndCreate(bytes32 preimageHash,address token,address claimAddress,address refundAddress,uint256 timelock,bytes32 callsHash)"));
        var preimageHashBytes = Convert.FromHexString(preimageHash.StartsWith("0x") ? preimageHash[2..] : preimageHash);
        var callsHashBytes = Convert.FromHexString(callsHash.StartsWith("0x") ? callsHash[2..] : callsHash);

        var witnessData = new byte[32 * 7];
        Array.Copy(witnessTypeHash, 0, witnessData, 0, 32);
        Array.Copy(PadTo32(preimageHashBytes), 0, witnessData, 32, 32);
        AbiEncodeAddress(lockToken).CopyTo(witnessData, 64);
        AbiEncodeAddress(claimAddress).CopyTo(witnessData, 96);
        AbiEncodeAddress(refundAddress).CopyTo(witnessData, 128);
        AbiEncodeUint256(new BigInteger(timelock)).CopyTo(witnessData, 160);
        Array.Copy(PadTo32(callsHashBytes), 0, witnessData, 192, 32);
        var witnessHash = sha3.CalculateHash(witnessData);

        // PermitWitnessTransferFrom struct hash
        var permitWitnessTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes(
                "PermitWitnessTransferFrom(TokenPermissions permitted,address spender,uint256 nonce,uint256 deadline,ExecuteAndCreate witness)ExecuteAndCreate(bytes32 preimageHash,address token,address claimAddress,address refundAddress,uint256 timelock,bytes32 callsHash)TokenPermissions(address token,uint256 amount)"));

        var structData = new byte[32 * 6];
        Array.Copy(permitWitnessTypeHash, 0, structData, 0, 32);
        Array.Copy(tokenPermHash, 0, structData, 32, 32);
        AbiEncodeAddress(coordinatorAddress).CopyTo(structData, 64);
        AbiEncodeUint256(nonce).CopyTo(structData, 96);
        AbiEncodeUint256(new BigInteger(deadline)).CopyTo(structData, 128);
        Array.Copy(witnessHash, 0, structData, 160, 32);
        var structHash = sha3.CalculateHash(structData);

        // Final EIP-712 digest
        var digestInput = new byte[2 + 32 + 32];
        digestInput[0] = 0x19;
        digestInput[1] = 0x01;
        Array.Copy(domainSeparator, 0, digestInput, 2, 32);
        Array.Copy(structHash, 0, digestInput, 34, 32);
        var digest = sha3.CalculateHash(digestInput);

        // Sign
        var ecPrivKey = ECPrivKey.Create(privateKey);
        ecPrivKey.TrySignRecoverable(digest, out var recSig);
        Scalar rScalar = default, sScalar = default;
        int recId = 0;
        recSig.Deconstruct(out rScalar, out sScalar, out recId);

        var sigBytes = new byte[65];
        rScalar.WriteToSpan(sigBytes.AsSpan(0, 32));
        sScalar.WriteToSpan(sigBytes.AsSpan(32, 32));
        sigBytes[64] = (byte)ValidateRecoveryId(recId);

        return "0x" + Convert.ToHexString(sigBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Signs an EIP-2612 Permit for token approval to Permit2.
    /// </summary>
    private static (int v, byte[] r, byte[] s) SignEip2612Permit(
        byte[] privateKey, string domainSeparatorHex,
        string owner, string spender, long nonce, long deadline)
    {
        var sha3 = new Sha3Keccack();
        var domainSeparator = Convert.FromHexString(
            domainSeparatorHex.StartsWith("0x") ? domainSeparatorHex[2..] : domainSeparatorHex);

        var permitTypeHash = sha3.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes(
                "Permit(address owner,address spender,uint256 value,uint256 nonce,uint256 deadline)"));

        // max uint256
        var maxValue = BigInteger.Pow(2, 256) - 1;

        var structData = new byte[32 * 6];
        Array.Copy(permitTypeHash, 0, structData, 0, 32);
        AbiEncodeAddress(owner).CopyTo(structData, 32);
        AbiEncodeAddress(spender).CopyTo(structData, 64);
        AbiEncodeUint256(maxValue).CopyTo(structData, 96);
        AbiEncodeUint256(new BigInteger(nonce)).CopyTo(structData, 128);
        AbiEncodeUint256(new BigInteger(deadline)).CopyTo(structData, 160);
        var structHash = sha3.CalculateHash(structData);

        var digestInput = new byte[2 + 32 + 32];
        digestInput[0] = 0x19;
        digestInput[1] = 0x01;
        Array.Copy(domainSeparator, 0, digestInput, 2, 32);
        Array.Copy(structHash, 0, digestInput, 34, 32);
        var digest = sha3.CalculateHash(digestInput);

        var ecPrivKey = ECPrivKey.Create(privateKey);
        ecPrivKey.TrySignRecoverable(digest, out var recSig);
        Scalar rScalar = default, sScalar = default;
        int recId = 0;
        recSig.Deconstruct(out rScalar, out sScalar, out recId);

        var rBytes = new byte[32];
        var sBytes = new byte[32];
        rScalar.WriteToSpan(rBytes);
        sScalar.WriteToSpan(sBytes);

        return (ValidateRecoveryId(recId), rBytes, sBytes);
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

        var v = ValidateRecoveryId(recId);
        return (v, rBytes, sBytes);
    }

    private static byte[] AbiEncodeUint256(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 32)
            throw new InvalidOperationException($"Value exceeds uint256 range ({bytes.Length} bytes)");
        var padded = new byte[32];
        Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
        return padded;
    }

    private static int ValidateRecoveryId(int recId)
    {
        if (recId is < 0 or > 1)
            throw new InvalidOperationException($"Invalid ECDSA recovery ID: {recId}. Expected 0 or 1.");
        return 27 + recId;
    }

    private static byte[] AbiEncodeAddress(string address)
    {
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            address = address[2..];
        var addressBytes = Convert.FromHexString(address);
        if (addressBytes.Length != 20)
            throw new InvalidOperationException($"EVM address must be 20 bytes, got {addressBytes.Length}: 0x{address}");
        var padded = new byte[32];
        Array.Copy(addressBytes, 0, padded, 12, 20);
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
