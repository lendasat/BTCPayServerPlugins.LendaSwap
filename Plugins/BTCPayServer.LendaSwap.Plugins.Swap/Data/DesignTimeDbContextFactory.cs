using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PluginDbContext>
{
    public PluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PluginDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new PluginDbContext(builder.Options, true);
    }
}
