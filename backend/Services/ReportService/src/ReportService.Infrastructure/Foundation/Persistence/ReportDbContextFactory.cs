using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReportService.Infrastructure.Persistence;

// 設計時（dotnet ef migrations）用の DbContext ファクトリ。マイグレーション生成が Program.cs の実行に依存しないようにする。
internal sealed class ReportDbContextFactory : IDesignTimeDbContextFactory<ReportDbContext>
{
    public ReportDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=report_svc;Username=ai;Password=ai")
            .Options;
        return new ReportDbContext(options);
    }
}
