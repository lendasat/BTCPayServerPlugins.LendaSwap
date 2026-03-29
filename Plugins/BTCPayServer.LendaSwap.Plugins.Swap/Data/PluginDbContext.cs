using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.LendaSwap.Plugins.Swap.Data;

public class PluginDbContext(DbContextOptions<PluginDbContext> options, bool designTime = false)
    : DbContext(options)
{
    public DbSet<SwapRecord> SwapRecords { get; set; }
    public DbSet<LendaSwapSetting> LendaSwapSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.LendaSwap.Plugins.Swap");

        SwapRecord.OnModelCreating(modelBuilder);
    }
}
