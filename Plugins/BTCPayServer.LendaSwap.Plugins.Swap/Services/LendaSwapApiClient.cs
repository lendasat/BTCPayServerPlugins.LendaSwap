using System;
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

    // --- Permit2 Funding Calldata ---

    public async Task<Permit2FundingCalldataResponse> GetPermit2FundingCalldata(
        string swapId, CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync($"/swap/{swapId}/swap-and-lock-calldata-permit2", ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<Permit2FundingCalldataResponse>(json);
    }

    // --- Gasless Fund ---

    public async Task<FundGaslessResponse> FundGasless(
        string swapId, FundGaslessRequest request, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync($"/swap/{swapId}/fund-gasless", ToJsonContent(request), ct);
        await EnsureSuccess(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<FundGaslessResponse>(json);
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
