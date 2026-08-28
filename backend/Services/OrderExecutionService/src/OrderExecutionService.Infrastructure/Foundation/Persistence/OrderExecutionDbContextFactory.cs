using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderExecutionService.Infrastructure.Persistence;

// 設計時（dotnet ef migrations）用の DbContext ファクトリ。マイグレーション生成が Program.cs の実行に依存しないようにする。
internal sealed class OrderExecutionDbContextFactory : IDesignTimeDbContextFactory<OrderExecutionDbContext>
{
    public OrderExecutionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseNpgsql("Host=localhost;Database=order_execution_svc;Username=ai;Password=ai")
            .Options;
        return new OrderExecutionDbContext(options);
    }
}
