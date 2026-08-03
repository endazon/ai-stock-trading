using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiStockTrading.MarketMonitor.Infrastructure.Foundation.Persistence;

// IADR-0012 踏襲: 設計時（dotnet ef migrations）用の DbContext ファクトリ。
// マイグレーション生成が Program.cs の実行に依存しないようにする。
internal sealed class MarketMonitorDbContextFactory : IDesignTimeDbContextFactory<MarketMonitorDbContext>
{
    public MarketMonitorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MarketMonitorDbContext>()
            .UseNpgsql("Host=localhost;Database=market_monitor_svc;Username=ai;Password=ai")
            .Options;
        return new MarketMonitorDbContext(options);
    }
}
