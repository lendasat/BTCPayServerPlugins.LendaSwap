using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class SwapResponseBase
{
    [JsonProperty("id")]
    public virtual string Id { get; set; }

    [JsonProperty("status")]
    public virtual string Status { get; set; }

    [JsonProperty("hash_lock")]
    public virtual string HashLock { get; set; }

    [JsonProperty("source_amount")]
    public virtual string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public virtual string TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public virtual TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public virtual TokenInfo TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public virtual long FeeSats { get; set; }

    [JsonProperty("created_at")]
    public virtual DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("network")]
    public virtual string Network { get; set; }
}

public class LightningToEvmSwapResponse : SwapResponseBase
{
    [JsonProperty("bolt11_invoice")]
    public string Bolt11Invoice { get; set; }

    [JsonProperty("boltz_invoice")]
    public string BoltzInvoice { get; set; }

    [JsonProperty("boltz_swap_id")]
    public string BoltzSwapId { get; set; }

    [JsonProperty("evm_htlc_address")]
    public string EvmHtlcAddress { get; set; }

    [JsonProperty("evm_coordinator_address")]
    public string EvmCoordinatorAddress { get; set; }

    [JsonProperty("evm_refund_locktime")]
    public long EvmRefundLocktime { get; set; }

    [JsonProperty("evm_expected_sats")]
    public long EvmExpectedSats { get; set; }

    [JsonProperty("evm_chain_id")]
    public long EvmChainId { get; set; }

    [JsonProperty("chain")]
    public string Chain { get; set; }

    [JsonProperty("client_evm_address")]
    public string ClientEvmAddress { get; set; }

    [JsonProperty("server_evm_address")]
    public string ServerEvmAddress { get; set; }

    [JsonProperty("wbtc_address")]
    public string WbtcAddress { get; set; }

    [JsonProperty("arkade_server_pk")]
    public string ArkadeServerPk { get; set; }

    [JsonProperty("receiver_pk")]
    public string ReceiverPk { get; set; }

    [JsonProperty("sender_pk")]
    public string SenderPk { get; set; }

    [JsonProperty("target_evm_address")]
    public string TargetEvmAddress { get; set; }

    [JsonProperty("btc_claim_txid")]
    public string BtcClaimTxid { get; set; }

    [JsonProperty("evm_claim_txid")]
    public string EvmClaimTxid { get; set; }

    [JsonProperty("evm_fund_txid")]
    public string EvmFundTxid { get; set; }

    [JsonProperty("vhtlc_refund_locktime")]
    public long VhtlcRefundLocktime { get; set; }

    [JsonProperty("unilateral_claim_delay")]
    public long UnilateralClaimDelay { get; set; }

    [JsonProperty("unilateral_refund_delay")]
    public long UnilateralRefundDelay { get; set; }

    [JsonProperty("unilateral_refund_without_receiver_delay")]
    public long UnilateralRefundWithoutReceiverDelay { get; set; }
}

public class BtcToArkadeSwapResponse : SwapResponseBase
{
    [JsonProperty("btc_htlc_address")]
    public string BtcHtlcAddress { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("btc_refund_locktime")]
    public long BtcRefundLocktime { get; set; }

    [JsonProperty("arkade_server_pk")]
    public string ArkadeServerPk { get; set; }

    [JsonProperty("arkade_vhtlc_address")]
    public string ArkadeVhtlcAddress { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("server_vhtlc_pk")]
    public string ServerVhtlcPk { get; set; }

    [JsonProperty("btc_claim_txid")]
    public string BtcClaimTxid { get; set; }

    [JsonProperty("btc_fund_txid")]
    public string BtcFundTxid { get; set; }

    [JsonProperty("arkade_claim_txid")]
    public string ArkadeClaimTxid { get; set; }

    [JsonProperty("arkade_fund_txid")]
    public string ArkadeFundTxid { get; set; }

    [JsonProperty("asset_amount")]
    public long AssetAmount { get; set; }

    [JsonProperty("vhtlc_refund_locktime")]
    public long VhtlcRefundLocktime { get; set; }

    [JsonProperty("unilateral_claim_delay")]
    public long UnilateralClaimDelay { get; set; }

    [JsonProperty("unilateral_refund_delay")]
    public long UnilateralRefundDelay { get; set; }

    [JsonProperty("unilateral_refund_without_receiver_delay")]
    public long UnilateralRefundWithoutReceiverDelay { get; set; }
}

public class LightningToArkadeSwapResponse : SwapResponseBase
{
    [JsonProperty("bolt11_invoice")]
    public string Bolt11Invoice { get; set; }

    [JsonProperty("boltz_invoice")]
    public string BoltzInvoice { get; set; }

    [JsonProperty("boltz_swap_id")]
    public string BoltzSwapId { get; set; }

    [JsonProperty("arkade_server_pk")]
    public string ArkadeServerPk { get; set; }

    [JsonProperty("arkade_vhtlc_address")]
    public string ArkadeVhtlcAddress { get; set; }

    [JsonProperty("receiver_pk")]
    public string ReceiverPk { get; set; }

    [JsonProperty("sender_pk")]
    public string SenderPk { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("client_lightning_invoice")]
    public string ClientLightningInvoice { get; set; }

    [JsonProperty("boltz_amount_sats")]
    public long BoltzAmountSats { get; set; }

    [JsonProperty("boltz_vhtlc_address")]
    public string BoltzVhtlcAddress { get; set; }

    [JsonProperty("arkade_claim_txid")]
    public string ArkadeClaimTxid { get; set; }

    [JsonProperty("arkade_fund_txid")]
    public string ArkadeFundTxid { get; set; }

    [JsonProperty("btc_claim_txid")]
    public string BtcClaimTxid { get; set; }

    [JsonProperty("vhtlc_refund_locktime")]
    public long VhtlcRefundLocktime { get; set; }

    [JsonProperty("unilateral_claim_delay")]
    public long UnilateralClaimDelay { get; set; }

    [JsonProperty("unilateral_refund_delay")]
    public long UnilateralRefundDelay { get; set; }

    [JsonProperty("unilateral_refund_without_receiver_delay")]
    public long UnilateralRefundWithoutReceiverDelay { get; set; }
}

public class EvmToLightningSwapResponse : SwapResponseBase
{
    [JsonProperty("evm_htlc_address")]
    public string EvmHtlcAddress { get; set; }

    [JsonProperty("evm_coordinator_address")]
    public string EvmCoordinatorAddress { get; set; }

    [JsonProperty("evm_chain_id")]
    public long EvmChainId { get; set; }

    [JsonProperty("evm_refund_locktime")]
    public long EvmRefundLocktime { get; set; }

    [JsonProperty("evm_expected_sats")]
    public long EvmExpectedSats { get; set; }

    [JsonProperty("client_evm_address")]
    public string ClientEvmAddress { get; set; }

    [JsonProperty("server_evm_address")]
    public string ServerEvmAddress { get; set; }

    [JsonProperty("client_lightning_invoice")]
    public string ClientLightningInvoice { get; set; }

    [JsonProperty("lightning_paid")]
    public bool LightningPaid { get; set; }

    [JsonProperty("evm_fund_txid")]
    public string EvmFundTxid { get; set; }

    [JsonProperty("evm_claim_txid")]
    public string EvmClaimTxid { get; set; }

    [JsonProperty("chain")]
    public string Chain { get; set; }

    [JsonProperty("gasless")]
    public bool Gasless { get; set; }
}

public class EvmToBitcoinSwapResponse : SwapResponseBase
{
    [JsonProperty("evm_hash_lock")]
    public string EvmHashLock { get; set; }

    [JsonProperty("btc_hash_lock")]
    public string BtcHashLock { get; set; }

    [JsonProperty("evm_htlc_address")]
    public string EvmHtlcAddress { get; set; }

    [JsonProperty("evm_coordinator_address")]
    public string EvmCoordinatorAddress { get; set; }

    [JsonProperty("evm_chain_id")]
    public long EvmChainId { get; set; }

    [JsonProperty("evm_refund_locktime")]
    public long EvmRefundLocktime { get; set; }

    [JsonProperty("evm_expected_sats")]
    public long EvmExpectedSats { get; set; }

    [JsonProperty("client_evm_address")]
    public string ClientEvmAddress { get; set; }

    [JsonProperty("server_evm_address")]
    public string ServerEvmAddress { get; set; }

    [JsonProperty("btc_htlc_address")]
    public string BtcHtlcAddress { get; set; }

    [JsonProperty("btc_user_claim_pk")]
    public string BtcUserClaimPk { get; set; }

    [JsonProperty("btc_server_refund_pk")]
    public string BtcServerRefundPk { get; set; }

    [JsonProperty("btc_refund_locktime")]
    public long BtcRefundLocktime { get; set; }

    [JsonProperty("btc_fund_txid")]
    public string BtcFundTxid { get; set; }

    [JsonProperty("btc_fund_vout")]
    public int? BtcFundVout { get; set; }

    [JsonProperty("btc_claim_txid")]
    public string BtcClaimTxid { get; set; }

    [JsonProperty("evm_fund_txid")]
    public string EvmFundTxid { get; set; }

    [JsonProperty("evm_claim_txid")]
    public string EvmClaimTxid { get; set; }

    [JsonProperty("target_btc_address")]
    public string TargetBtcAddress { get; set; }

    [JsonProperty("chain")]
    public string Chain { get; set; }

    [JsonProperty("gasless")]
    public bool Gasless { get; set; }
}

public class GetSwapResponse : SwapResponseBase
{
    [JsonProperty("direction")]
    public string Direction { get; set; }

    // Lightning fields
    [JsonProperty("bolt11_invoice")]
    public string Bolt11Invoice { get; set; }

    [JsonProperty("boltz_invoice")]
    public string BoltzInvoice { get; set; }

    [JsonProperty("client_lightning_invoice")]
    public string ClientLightningInvoice { get; set; }

    [JsonProperty("lightning_paid")]
    public bool? LightningPaid { get; set; }

    // Bitcoin HTLC fields
    [JsonProperty("btc_htlc_address")]
    public string BtcHtlcAddress { get; set; }

    [JsonProperty("btc_refund_locktime")]
    public long? BtcRefundLocktime { get; set; }

    // EVM fields
    [JsonProperty("evm_htlc_address")]
    public string EvmHtlcAddress { get; set; }

    [JsonProperty("evm_refund_locktime")]
    public long? EvmRefundLocktime { get; set; }

    [JsonProperty("evm_chain_id")]
    public long? EvmChainId { get; set; }

    [JsonProperty("evm_coordinator_address")]
    public string EvmCoordinatorAddress { get; set; }

    [JsonProperty("evm_expected_sats")]
    public long? EvmExpectedSats { get; set; }

    [JsonProperty("client_evm_address")]
    public string ClientEvmAddress { get; set; }

    [JsonProperty("server_evm_address")]
    public string ServerEvmAddress { get; set; }

    [JsonProperty("wbtc_address")]
    public string WbtcAddress { get; set; }

    // EVM->Bitcoin specific fields
    [JsonProperty("btc_user_claim_pk")]
    public string BtcUserClaimPk { get; set; }

    [JsonProperty("btc_server_refund_pk")]
    public string BtcServerRefundPk { get; set; }

    [JsonProperty("btc_hash_lock")]
    public string BtcHashLock { get; set; }

    [JsonProperty("btc_fund_vout")]
    public int? BtcFundVout { get; set; }

    [JsonProperty("target_btc_address")]
    public string TargetBtcAddress { get; set; }

    // Arkade fields
    [JsonProperty("arkade_vhtlc_address")]
    public string ArkadeVhtlcAddress { get; set; }

    [JsonProperty("arkade_server_pk")]
    public string ArkadeServerPk { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("vhtlc_refund_locktime")]
    public long? VhtlcRefundLocktime { get; set; }

    // Transaction IDs
    [JsonProperty("evm_claim_txid")]
    public string EvmClaimTxid { get; set; }

    [JsonProperty("evm_fund_txid")]
    public string EvmFundTxid { get; set; }

    [JsonProperty("btc_claim_txid")]
    public string BtcClaimTxid { get; set; }

    [JsonProperty("btc_fund_txid")]
    public string BtcFundTxid { get; set; }

    [JsonProperty("arkade_claim_txid")]
    public string ArkadeClaimTxid { get; set; }

    [JsonProperty("arkade_fund_txid")]
    public string ArkadeFundTxid { get; set; }
}

public class Permit2FundingCalldataResponse
{
    [JsonProperty("coordinator_address")]
    public string CoordinatorAddress { get; set; }

    [JsonProperty("permit2_address")]
    public string Permit2Address { get; set; }

    [JsonProperty("source_token_address")]
    public string SourceTokenAddress { get; set; }

    [JsonProperty("source_amount")]
    public long SourceAmount { get; set; }

    [JsonProperty("lock_token_address")]
    public string LockTokenAddress { get; set; }

    [JsonProperty("preimage_hash")]
    public string PreimageHash { get; set; }

    [JsonProperty("claim_address")]
    public string ClaimAddress { get; set; }

    [JsonProperty("timelock")]
    public long Timelock { get; set; }

    [JsonProperty("calls")]
    public List<Permit2Call> Calls { get; set; }

    [JsonProperty("calls_hash")]
    public string CallsHash { get; set; }

    [JsonProperty("eip2612")]
    public Eip2612Data Eip2612 { get; set; }

    [JsonProperty("relay_fee")]
    public string RelayFee { get; set; }
}

public class Permit2Call
{
    [JsonProperty("target")]
    public string Target { get; set; }

    [JsonProperty("value")]
    public string Value { get; set; }

    [JsonProperty("call_data")]
    public string CallData { get; set; }
}

public class Eip2612Data
{
    [JsonProperty("supported")]
    public bool Supported { get; set; }

    [JsonProperty("already_approved")]
    public bool AlreadyApproved { get; set; }

    [JsonProperty("nonce")]
    public long Nonce { get; set; }

    [JsonProperty("domain_separator")]
    public string DomainSeparator { get; set; }
}

public class FundGaslessResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("tx_hash")]
    public string TxHash { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }
}

public class ClaimGaslessResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("tx_hash")]
    public string TxHash { get; set; }
}

public class QuoteResponse
{
    [JsonProperty("exchange_rate")]
    public string ExchangeRate { get; set; }

    [JsonProperty("max_amount")]
    public long MaxAmount { get; set; }

    [JsonProperty("min_amount")]
    public long MinAmount { get; set; }

    [JsonProperty("network_fee")]
    public long NetworkFee { get; set; }

    [JsonProperty("gasless_network_fee")]
    public long GaslessNetworkFee { get; set; }

    [JsonProperty("protocol_fee")]
    public long ProtocolFee { get; set; }

    [JsonProperty("protocol_fee_rate")]
    public double ProtocolFeeRate { get; set; }

    [JsonProperty("source_amount")]
    public string SourceAmountCalculated { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmountCalculated { get; set; }
}

public class RedeemAndSwapCalldataResponse
{
    [JsonProperty("calls_hash")]
    public string CallsHash { get; set; }

    [JsonProperty("dex_calldata")]
    public DexCalldata DexCalldata { get; set; }

    [JsonProperty("gasless_fee_sats")]
    public long GaslessFeeSats { get; set; }
}

public class ErrorResponse
{
    [JsonProperty("error")]
    public string Error { get; set; }
}

public class LendaSwapApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public class TokenInfosResponse
{
    [JsonProperty("btc_tokens")]
    public List<TokenInfo> BtcTokens { get; set; } = new();

    [JsonProperty("evm_tokens")]
    public List<TokenInfo> EvmTokens { get; set; } = new();
}

public class TokenInfo
{
    [JsonProperty("chain")]
    public string Chain { get; set; }

    [JsonProperty("decimals")]
    public int Decimals { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("symbol")]
    public string Symbol { get; set; }

    [JsonProperty("token_id")]
    public string TokenId { get; set; }
}
