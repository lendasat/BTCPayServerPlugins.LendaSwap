using System;
using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Crypto;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class SwapCryptoHelper
{
    public byte[] GeneratePreimage()
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    public string ComputePaymentHash(byte[] preimage)
    {
        var hash = SHA256.HashData(preimage);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string ComputeHash160(byte[] preimage)
    {
        var hash = Hashes.Hash160(preimage);
        return Convert.ToHexString(hash.ToBytes()).ToLowerInvariant();
    }
}
