using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Services;

public class LendaSwapApiClient(HttpClient httpClient)
{
    private static StringContent ToJsonContent(object obj) =>
        new(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var statusCode = (int)response.StatusCode;

        // Try to extract error message from API response body
        string errorMessage = null;
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var errorObj = JsonConvert.DeserializeObject<ErrorResponse>(body);
                errorMessage = errorObj?.Error;
            }
            catch
            {
                // Response isn't JSON or doesn't match ErrorResponse shape
            }
        }

        // Use the API error message if available, otherwise include the raw body
        if (!string.IsNullOrEmpty(errorMessage))
            throw new LendaSwapApiException(statusCode, errorMessage);

        if (!string.IsNullOrEmpty(body) && body.Length <= 500)
            throw new LendaSwapApiException(statusCode, $"API error {statusCode}: {body}");

        throw new LendaSwapApiException(statusCode, $"API error {statusCode} ({response.ReasonPhrase})");
    }

    // --- Swap Creation Endpoints ---

    public async Task<LightningToEvmSwapResponse> CreateLightningToEvmSwap(
        LightningToEvmSwapRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/lightning/evm", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<LightningToEvmSwapResponse>(json);
    }

    public async Task<EvmToBitcoinSwapResponse> CreateBitcoinToEvmSwap(
        LightningToEvmSwapRequest request, CancellationToken ct = default)
    {
        // Same request structure as Lightning→EVM but different endpoint + response has btc_htlc_address
        var response = await httpClient.PostAsync("/swap/bitcoin/evm", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<EvmToBitcoinSwapResponse>(json);
    }

    public async Task<BtcToArkadeSwapResponse> CreateBitcoinToArkadeSwap(
        BitcoinToArkadeSwapRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/bitcoin/arkade", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<BtcToArkadeSwapResponse>(json);
    }

    public async Task<LightningToArkadeSwapResponse> CreateLightningToArkadeSwap(
        LightningToArkadeSwapRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/lightning/arkade", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<LightningToArkadeSwapResponse>(json);
    }

    public async Task<EvmToLightningSwapResponse> CreateEvmToLightningSwap(
        EvmToLightningSwapRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/evm/lightning", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<EvmToLightningSwapResponse>(json);
    }

    public async Task<EvmToBitcoinSwapResponse> CreateEvmToBitcoinSwap(
        EvmToBitcoinSwapRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/evm/bitcoin", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<EvmToBitcoinSwapResponse>(json);
    }

    // --- Swap Status ---

    public async Task<GetSwapResponse> GetSwapStatus(string swapId, CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync($"/swap/{swapId}", ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<GetSwapResponse>(json);
    }

    // --- Gasless Claim (was Gelato) ---

    public async Task<ClaimGaslessResponse> ClaimGasless(
        string swapId, ClaimGaslessRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync($"/swap/{swapId}/claim-gasless", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<ClaimGaslessResponse>(json);
    }

    // --- Tokens (was asset-pairs) ---

    public async Task<TokenInfosResponse> GetTokens(CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync("/tokens", ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<TokenInfosResponse>(json) ?? new TokenInfosResponse();
    }

    // --- Quote ---

    public async Task<QuoteResponse> GetQuote(
        string sourceChain, string sourceToken, string targetChain, string targetToken,
        long? sourceAmount = null, long? targetAmount = null, CancellationToken ct = default)
    {
        var url = $"/quote?source_chain={Uri.EscapeDataString(sourceChain)}" +
                  $"&source_token={Uri.EscapeDataString(sourceToken)}" +
                  $"&target_chain={Uri.EscapeDataString(targetChain)}" +
                  $"&target_token={Uri.EscapeDataString(targetToken)}";

        if (sourceAmount.HasValue)
            url += $"&source_amount={sourceAmount.Value}";
        else if (targetAmount.HasValue)
            url += $"&target_amount={targetAmount.Value}";

        var response = await httpClient.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<QuoteResponse>(json);
    }

    // --- Redeem Calldata (for gasless claims) ---

    public async Task<RedeemAndSwapCalldataResponse> GetRedeemAndSwapCalldata(
        string swapId, string destination, CancellationToken ct = default)
    {
        var url = $"/swap/{swapId}/redeem-and-swap-calldata?destination={Uri.EscapeDataString(destination)}";
        var response = await httpClient.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<RedeemAndSwapCalldataResponse>(json);
    }
}

#region Request Models

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

#endregion

#region Response Models

public class LightningToEvmSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

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

    [JsonProperty("source_amount")]
    public string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public TokenInfo TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("arkade_server_pk")]
    public string ArkadeServerPk { get; set; }

    [JsonProperty("receiver_pk")]
    public string ReceiverPk { get; set; }

    [JsonProperty("sender_pk")]
    public string SenderPk { get; set; }

    [JsonProperty("target_evm_address")]
    public string TargetEvmAddress { get; set; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("network")]
    public string Network { get; set; }

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

public class BtcToArkadeSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("btc_htlc_address")]
    public string BtcHtlcAddress { get; set; }

    [JsonProperty("source_amount")]
    public string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmount { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("source_token")]
    public TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public TokenInfo TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

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

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("network")]
    public string Network { get; set; }

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

public class LightningToArkadeSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

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

    [JsonProperty("source_amount")]
    public string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public TokenInfo TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("receiver_pk")]
    public string ReceiverPk { get; set; }

    [JsonProperty("sender_pk")]
    public string SenderPk { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("network")]
    public string Network { get; set; }

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

public class EvmToLightningSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("source_amount")]
    public string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public TokenInfo TargetToken { get; set; }

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

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("network")]
    public string Network { get; set; }

    [JsonProperty("gasless")]
    public bool Gasless { get; set; }
}

public class EvmToBitcoinSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("evm_hash_lock")]
    public string EvmHashLock { get; set; }

    [JsonProperty("btc_hash_lock")]
    public string BtcHashLock { get; set; }

    [JsonProperty("source_amount")]
    public string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public TokenInfo TargetToken { get; set; }

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

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("network")]
    public string Network { get; set; }

    [JsonProperty("gasless")]
    public bool Gasless { get; set; }
}

public class GetSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("direction")]
    public string Direction { get; set; }

    [JsonProperty("source_amount")]
    public string SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public string TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public TokenInfo SourceToken { get; set; }

    [JsonProperty("target_token")]
    public TokenInfo TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

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

    // EVM→Bitcoin specific fields
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

    [JsonProperty("network")]
    public string Network { get; set; }
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

#endregion
