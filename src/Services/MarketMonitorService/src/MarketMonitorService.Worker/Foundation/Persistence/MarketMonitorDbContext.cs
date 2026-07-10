using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.MarketMonitor.Worker.Foundation.Persistence;

// ADR-0001（Database per Service）, IADR-0012 踏襲: 市場監視サービス専有の DbContext。
internal sealed class MarketMonitorDbContext(DbContextOptions<MarketMonitorDbContext> options)
    : DbContext(options)
{
    public DbSet<MonitorSettingsRow> MonitorSettings => Set<MonitorSettingsRow>();

    public DbSet<PriceBaselineRow> PriceBaselines => Set<PriceBaselineRow>();

    public DbSet<CooldownRow> Cooldowns => Set<CooldownRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<MonitorSettingsRow>(e =>
        {
            e.ToTable("monitor_settings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Json).HasColumnType("jsonb").IsRequired();
            e.Property(r => r.Version).IsConcurrencyToken();
        });

        mb.Entity<PriceBaselineRow>(e =>
        {
            e.ToTable("price_baseline");
            e.HasKey(r => new { r.Symbol, r.Market });
            e.Property(r => r.Symbol).HasMaxLength(32);
        });

        mb.Entity<CooldownRow>(e =>
        {
            e.ToTable("cooldown");
            e.HasKey(r => new { r.Symbol, r.Market });
            e.Property(r => r.Symbol).HasMaxLength(32);
        });
    }
}
