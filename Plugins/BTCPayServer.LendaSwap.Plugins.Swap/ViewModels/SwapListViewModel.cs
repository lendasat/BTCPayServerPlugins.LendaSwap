using System.Collections.Generic;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;

namespace BTCPayServer.LendaSwap.Plugins.Swap.ViewModels;

public class SwapListViewModel
{
    public List<SwapRecord> Swaps { get; set; } = new();
    public SwapStatus? StatusFilter { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int TotalCount { get; set; }
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
}
