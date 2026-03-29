using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.LendaSwap.Plugins.Swap.Data;
using BTCPayServer.LendaSwap.Plugins.Swap.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.LendaSwap.Plugins.Swap;

public class LendaSwapPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("store-integrations-nav", "LendaSwapNav");

        services.AddSingleton<PluginDbContextFactory>();
        services.AddDbContext<PluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<PluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        services.AddHostedService<PluginMigrationRunner>();

        services.AddSingleton<SwapCryptoHelper>();
        services.AddScoped<SwapService>();
        services.AddScoped<TaprootHtlcClaimService>();
        services.AddScoped<EvmGaslessClaimService>();

        services.AddHttpClient<LendaSwapApiClient>((provider, client) =>
        {
            var apiUrl = Environment.GetEnvironmentVariable("LENDASWAP_API_URL");
            client.BaseAddress = new Uri(string.IsNullOrEmpty(apiUrl) ? "http://localhost:3333/" : apiUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScheduledTask<SwapStatusMonitor>(TimeSpan.FromSeconds(2));

        base.Execute(services);
    }
}
