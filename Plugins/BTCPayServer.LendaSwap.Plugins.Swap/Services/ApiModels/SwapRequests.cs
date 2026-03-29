using Newtonsoft.Json;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class LightningToEvmSwapRequest
{
    [JsonProperty("amount_in")]
    public long? AmountIn { get; set; }

    [JsonProperty("amount_out")]
    public long? AmountOut { get; set; }

    [JsonProperty("claiming_address")]
    public string ClaimingAddress { get; set; }

    [JsonProperty("evm_chain_id")]
    public long EvmChainId { get; set; }

    [JsonProperty("gasless")]
    public bool? Gasless { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }

    [JsonProperty("refund_pk")]
    public string RefundPk { get; set; }

    [JsonProperty("target_address")]
    public string TargetAddress { get; set; }

    [JsonProperty("token_address")]
    public string TokenAddress { get; set; }

    [JsonProperty("user_id")]
    public string UserId { get; set; }
}

public class BitcoinToArkadeSwapRequest
{
    [JsonProperty("claim_pk")]
    public string ClaimPk { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }

    [JsonProperty("refund_pk")]
    public string RefundPk { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("user_id")]
    public string UserId { get; set; }
}

public class LightningToArkadeSwapRequest
{
    [JsonProperty("claim_pk")]
    public string ClaimPk { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("user_id")]
    public string UserId { get; set; }
}

public class EvmToLightningSwapRequest
{
    [JsonProperty("evm_chain_id")]
    public long EvmChainId { get; set; }

    [JsonProperty("token_address")]
    public string TokenAddress { get; set; }

    [JsonProperty("user_address")]
    public string UserAddress { get; set; }

    [JsonProperty("lightning_invoice")]
    public string LightningInvoice { get; set; }

    [JsonProperty("lightning_address")]
    public string LightningAddress { get; set; }

    [JsonProperty("amount_sats")]
    public long? AmountSats { get; set; }

    [JsonProperty("user_id")]
    public string UserId { get; set; }

    [JsonProperty("gasless")]
    public bool Gasless { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }
}

public class EvmToBitcoinSwapRequest
{
    [JsonProperty("evm_chain_id")]
    public long EvmChainId { get; set; }

    [JsonProperty("token_address")]
    public string TokenAddress { get; set; }

    [JsonProperty("user_address")]
    public string UserAddress { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("claim_pk")]
    public string ClaimPk { get; set; }

    [JsonProperty("user_id")]
    public string UserId { get; set; }

    [JsonProperty("amount_in")]
    public long? AmountIn { get; set; }

    [JsonProperty("amount_out")]
    public long? AmountOut { get; set; }

    [JsonProperty("target_address")]
    public string TargetAddress { get; set; }

    [JsonProperty("gasless")]
    public bool Gasless { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }
}

public class ClaimGaslessRequest
{
    [JsonProperty("secret")]
    public string Secret { get; set; }

    [JsonProperty("destination")]
    public string Destination { get; set; }

    [JsonProperty("v")]
    public int V { get; set; }

    [JsonProperty("r")]
    public string R { get; set; }

    [JsonProperty("s")]
    public string S { get; set; }

    [JsonProperty("dex_calldata")]
    public DexCalldata DexCalldata { get; set; }
}

public class DexCalldata
{
    [JsonProperty("to")]
    public string To { get; set; }

    [JsonProperty("data")]
    public string Data { get; set; }

    [JsonProperty("value")]
    public string Value { get; set; }
}
