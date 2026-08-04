using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// IADR-0012: 設計時（dotnet ef migrations）用の DbContext ファクトリ。マイグレーション生成が Program.cs の
// 実行（ホスト起動・メッセージング等）に依存しないようにする。実行時の接続は Program.cs の構成が用いる。
internal sealed class RiskManagementDbContextFactory : IDesignTimeDbContextFactory<RiskManagementDbContext>
{
    public RiskManagementDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RiskManagementDbContext>()
            // 設計時はスキーマ生成のみで DB へ接続しない。実値は実行時構成（DefaultConnection）が供給する。
            .UseNpgsql("Host=localhost;Database=risk_management_svc;Username=ai;Password=ai")
            .Options;
        return new RiskManagementDbContext(options);
    }
}
