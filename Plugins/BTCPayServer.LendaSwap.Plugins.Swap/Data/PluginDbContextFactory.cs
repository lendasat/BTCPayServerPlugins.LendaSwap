using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data;

public class PluginDbContextFactory(IOptions<DatabaseOptions> options)
    : BaseDbContextFactory<PluginDbContext>(options,
        "BTCPayServer.LendaSwap.Plugins.Swap")
{
    public override PluginDbContext CreateContext(
        Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<PluginDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new PluginDbContext(builder.Options);
    }

    public async Task<LendaSwapSettings> GetSettingAsync(string storeId)
    {
        await using var db = CreateContext();
        return await db.GetSettingAsync(storeId);
    }

    public async Task<LendaSwapSettings> SetSettingAsync(string storeId, LendaSwapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var db = CreateContext();
        var existingSetting = await db.LendaSwapSettings.FirstOrDefaultAsync(a => a.StoreId == storeId);

        if (existingSetting != null)
        {
            existingSetting.Setting = JsonConvert.SerializeObject(settings);
        }
        else
        {
            var newSetting = new LendaSwapSetting
            {
                StoreId = storeId,
                Setting = JsonConvert.SerializeObject(settings)
            };
            db.LendaSwapSettings.Add(newSetting);
        }

        await db.SaveChangesAsync();
        return settings;
    }
}
