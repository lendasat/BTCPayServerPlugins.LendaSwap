using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.LendaSwap.Plugins.Swap.ViewModels;

public class LendaSwapSettingsViewModel
{
    [Display(Name = "Default Polygon Address")]
    public string DefaultPolygonAddress { get; set; }

    [Display(Name = "Default Arkade Address")]
    public string DefaultArkadeAddress { get; set; }
}
