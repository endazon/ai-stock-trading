using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiStockTrading.Configuration.Worker.Foundation.Persistence;

// 設計時（dotnet ef migrations）用の DbContext ファクトリ。マイグレーション生成が Program.cs の実行に依存しないようにする。
internal sealed class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConfigurationDbContext>
{
    public ConfigurationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseNpgsql("Host=localhost;Database=configuration_svc;Username=ai;Password=ai")
            .Options;
        return new ConfigurationDbContext(options);
    }
}
