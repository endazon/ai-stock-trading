using AiStockTrading.Report.Application.Adapters;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using AiStockTrading.Report.Worker.Composable.Adapters;
using AiStockTrading.Report.Worker.Composable.Polling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiStockTrading.Report.Worker.Tests;

// FR-06, IADR-0115 決定5/6, #280: 自動生成スケジューラと約定供給の結線を検証する。
// 既定（未設定）は「常駐なし・約定は no-op」＝現行挙動とバイト等価であることを固定する。
public class ReportAutoGenerationWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private static bool HasScheduler(IServiceProvider services) =>
        services.GetServices<IHostedService>().Any(s => s is ReportAutoGenerationService);

    [Fact]
    public void 既定では自動生成の常駐が登録されない()
    {
        // opt-in（Reports:AutoGeneration:Enabled 未設定）＝現行挙動。
        HasScheduler(factory.Services).Should().BeFalse();
    }

    [Fact]
    public void 有効化すると自動生成の常駐が登録される()
    {
        using var enabled = factory.WithWebHostBuilder(b => b.UseSetting("Reports:AutoGeneration:Enabled", "true"));

        HasScheduler(enabled.Services).Should().BeTrue();
    }

    [Fact]
    public void 既定の約定供給は_no_op()
    {
        // RiskManagement:BaseUrl 未設定＝権威源へ 1 リクエストも出さない（数値 0 の報告書）。
        factory.Services.GetRequiredService<IPeriodFillSource>().Should().BeOfType<NoOpPeriodFillSource>();
    }

    [Fact]
    public void 権威源の_BaseUrl_を設定すると_s2s_照会の実装が選ばれる()
    {
        using var wired = factory.WithWebHostBuilder(b =>
            b.UseSetting("RiskManagement:BaseUrl", "http://risk-management-service"));

        wired.Services.GetRequiredService<IPeriodFillSource>().Should().BeOfType<HttpPeriodFillSource>();
    }

    [Fact]
    public void 不正な_BaseUrl_は_no_op_へ倒す()
    {
        using var broken = factory.WithWebHostBuilder(b =>
            b.UseSetting("RiskManagement:BaseUrl", "not-a-uri"));

        broken.Services.GetRequiredService<IPeriodFillSource>().Should().BeOfType<NoOpPeriodFillSource>();
    }

    [Fact]
    public async Task 常駐の一巡回は実_DI_経由で生成し提示までで止まる()
    {
        // scoped な EF ストア・ドラフト生成を巡回ごとのスコープで解決できることと、確定へ進まないことを
        // 実際の DI（Program.cs の配線）で確認する。日報は常に直近営業日が対象になるため必ず 1 件以上生成される。
        using var enabled = factory.WithWebHostBuilder(b => b.UseSetting("Reports:AutoGeneration:Enabled", "true"));
        var scheduler = enabled.Services.GetServices<IHostedService>().OfType<ReportAutoGenerationService>().Single();

        await scheduler.RunOnceAsync(CancellationToken.None);

        using var scope = enabled.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        var reports = store.List();

        reports.Should().NotBeEmpty();
        reports.Should().OnlyContain(r => r.State == ReportState.Draft && r.ConfirmedAt == null);
        reports.Should().OnlyContain(r => store.GetReview(r.PeriodKey)!.State == ReviewState.PendingApproval);
        reports.Should().OnlyContain(r => r.Body.Contains("report_type:"));
    }
}
