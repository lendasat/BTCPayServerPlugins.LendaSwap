using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.LendaSwap.Plugins.Swap.Services;

namespace BTCPayServer.LendaSwap.Plugins.Swap.ViewModels;

public class CreateSwapViewModel
{
    [Required]
    [Display(Name = "Source Chain")]
    public string SourceChain { get; set; }

    [Required]
    [Display(Name = "Source Token")]
    public string SourceToken { get; set; }

    [Required]
    [Display(Name = "Target Chain")]
    public string TargetChain { get; set; }

    [Required]
    [Display(Name = "Target Token")]
    public string TargetToken { get; set; }

    [Required]
    [Display(Name = "Amount (sats)")]
    [Range(1, long.MaxValue, ErrorMessage = "Amount must be a positive number")]
    public long AmountSats { get; set; }

    [Required]
    [Display(Name = "Destination Address")]
    public string ClaimDestination { get; set; }

    // Display-only fields populated by controller GET
    public List<TokenInfo> BtcTokens { get; set; } = new();
    public List<TokenInfo> EvmTokens { get; set; } = new();
    public string QuoteExchangeRate { get; set; }
    public long? QuoteFeeSats { get; set; }
}
