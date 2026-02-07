using System;
using System.Collections.Generic;
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

    public async Task<BtcToEvmSwapResponse> CreateLightningToUsdcSwap(
        CreateLightningToPolygonRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/lightning/polygon", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<BtcToEvmSwapResponse>(json);
    }

    public async Task<BtcToArkadeSwapResponse> CreateBitcoinToArkadeSwap(
        CreateBitcoinToArkadeRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/swap/bitcoin/arkade", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<BtcToArkadeSwapResponse>(json);
    }

    public async Task<GetSwapResponse> GetSwapStatus(string swapId, CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync($"/swap/{swapId}", ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<GetSwapResponse>(json);
    }

    public async Task<ClaimGelatoResponse> ClaimViaGelato(
        string swapId, string secret, CancellationToken ct = default)
    {
        var payload = new ClaimGelatoRequest { Secret = secret };
        var response = await httpClient.PostAsync($"/swap/{swapId}/claim-gelato", ToJsonContent(payload), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<ClaimGelatoResponse>(json);
    }

    public async Task<List<AssetPairResponse>> GetAssetPairs(CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync("/asset-pairs", ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<List<AssetPairResponse>>(json);
    }

    public async Task<QuoteResponse> GetQuote(
        string fromToken, string toToken, long baseAmountSats, CancellationToken ct = default)
    {
        var url = $"/quote?from={Uri.EscapeDataString(fromToken)}&to={Uri.EscapeDataString(toToken)}&base_amount={baseAmountSats}";
        var response = await httpClient.GetAsync(url, ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<QuoteResponse>(json);
    }
}

#region Request Models

public class CreateLightningToPolygonRequest
{
    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("refund_pk")]
    public string RefundPk { get; set; }

    [JsonProperty("source_amount")]
    public long? SourceAmount { get; set; }

    [JsonProperty("target_address")]
    public string TargetAddress { get; set; }

    [JsonProperty("target_token")]
    public string TargetToken { get; set; } = "usdc_pol";

    [JsonProperty("user_id")]
    public string UserId { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }
}

public class CreateBitcoinToArkadeRequest
{
    [JsonProperty("claim_pk")]
    public string ClaimPk { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("refund_pk")]
    public string RefundPk { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("target_arkade_address")]
    public string TargetArkadeAddress { get; set; }

    [JsonProperty("user_id")]
    public string UserId { get; set; }

    [JsonProperty("referral_code")]
    public string ReferralCode { get; set; }
}

public class ClaimGelatoRequest
{
    [JsonProperty("secret")]
    public string Secret { get; set; }
}

#endregion

#region Response Models

public class BtcToEvmSwapResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("ln_invoice")]
    public string LnInvoice { get; set; }

    [JsonProperty("htlc_address_evm")]
    public string HtlcAddressEvm { get; set; }

    [JsonProperty("htlc_address_arkade")]
    public string HtlcAddressArkade { get; set; }

    [JsonProperty("source_amount")]
    public long SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public double TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public string SourceToken { get; set; }

    [JsonProperty("target_token")]
    public string TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("evm_refund_locktime")]
    public int EvmRefundLocktime { get; set; }

    [JsonProperty("user_address_evm")]
    public string UserAddressEvm { get; set; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("network")]
    public string Network { get; set; }

    [JsonProperty("bitcoin_htlc_claim_txid")]
    public string BitcoinHtlcClaimTxid { get; set; }

    [JsonProperty("bitcoin_htlc_fund_txid")]
    public string BitcoinHtlcFundTxid { get; set; }

    [JsonProperty("evm_htlc_claim_txid")]
    public string EvmHtlcClaimTxid { get; set; }

    [JsonProperty("evm_htlc_fund_txid")]
    public string EvmHtlcFundTxid { get; set; }
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
    public long SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public long TargetAmount { get; set; }

    [JsonProperty("sats_receive")]
    public long SatsReceive { get; set; }

    [JsonProperty("source_token")]
    public string SourceToken { get; set; }

    [JsonProperty("target_token")]
    public string TargetToken { get; set; }

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
    public long SourceAmount { get; set; }

    [JsonProperty("target_amount")]
    public double TargetAmount { get; set; }

    [JsonProperty("source_token")]
    public string SourceToken { get; set; }

    [JsonProperty("target_token")]
    public string TargetToken { get; set; }

    [JsonProperty("fee_sats")]
    public long FeeSats { get; set; }

    [JsonProperty("hash_lock")]
    public string HashLock { get; set; }

    [JsonProperty("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonProperty("ln_invoice")]
    public string LnInvoice { get; set; }

    [JsonProperty("btc_htlc_address")]
    public string BtcHtlcAddress { get; set; }

    [JsonProperty("htlc_address_evm")]
    public string HtlcAddressEvm { get; set; }

    [JsonProperty("evm_htlc_claim_txid")]
    public string EvmHtlcClaimTxid { get; set; }

    [JsonProperty("bitcoin_htlc_claim_txid")]
    public string BitcoinHtlcClaimTxid { get; set; }

    [JsonProperty("btc_claim_txid")]
    public string BtcClaimTxid { get; set; }

    [JsonProperty("arkade_claim_txid")]
    public string ArkadeClaimTxid { get; set; }
}

public class ClaimGelatoResponse
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("task_id")]
    public string TaskId { get; set; }

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

    [JsonProperty("protocol_fee")]
    public long ProtocolFee { get; set; }

    [JsonProperty("protocol_fee_rate")]
    public double ProtocolFeeRate { get; set; }
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

public class AssetPairResponse
{
    [JsonProperty("source")]
    public TokenInfo Source { get; set; }

    [JsonProperty("target")]
    public TokenInfo Target { get; set; }
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
