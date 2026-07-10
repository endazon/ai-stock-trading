using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// 設計時（dotnet ef migrations）用の DbContext ファクトリ。マイグレーション生成が Program.cs の実行に依存しないようにする。
internal sealed class CostControlDbContextFactory : IDesignTimeDbContextFactory<CostControlDbContext>
{
    public CostControlDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CostControlDbContext>()
            .UseNpgsql("Host=localhost;Database=cost_control_svc;Username=ai;Password=ai")
            .Options;
        return new CostControlDbContext(options);
    }
}
