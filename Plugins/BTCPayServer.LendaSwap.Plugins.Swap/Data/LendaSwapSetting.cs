using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data;

public class LendaSwapSetting
{
    [Key]
    [MaxLength(50)]
    public string StoreId { get; set; }

    public string Setting { get; set; }
}
