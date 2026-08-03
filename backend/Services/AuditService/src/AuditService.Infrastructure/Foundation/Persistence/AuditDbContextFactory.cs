using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiStockTrading.Audit.Infrastructure.Foundation.Persistence;

// 設計時（dotnet ef migrations）用の DbContext ファクトリ。マイグレーション生成が Program.cs の実行に依存しないようにする。
internal sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=localhost;Database=audit_svc;Username=ai;Password=ai")
            .Options;
        return new AuditDbContext(options);
    }
}
