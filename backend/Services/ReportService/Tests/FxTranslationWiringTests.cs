using ReportService.Infrastructure.ExternalServices;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #611, ADR-0022, IADR-0282 決定2: 期末レートの供給が**composition root で実際に結線されている**ことを固定する。
//
// 🔴 **コンストラクタへ直接値を渡す単体テストでは、本番の配線が切れていても緑になる。**
// ポートは安全既定（Unsupplied）を持つため、配線を落としてもアダプタ単体のテストは通り、
// **本番だけが「供給されていません」を出し続ける**（#563 が実際にその形で 1 度起きている）。
public class FxTranslationWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    // 🔴 **否定形（安全既定）**: Fx:Provider が無い構成では未供給の実装へ倒す（0 円を返す実装へ倒してはならない）。
    [Fact]
    public void 為替レート源が未設定なら未供給の実装へ倒す()
    {
        factory.Services.GetRequiredService<IFxRateSource>().Should().BeOfType<NoOpFxRateSource>();
        factory.Services.GetRequiredService<IPeriodEndFxRateSource>()
            .Should().BeOfType<UnsuppliedPeriodEndFxRateSource>();
    }

    // 🔴 **否定形**: 「有効化したつもり」（provider 指定だが鍵無し）も未供給へ倒す（判断サービスと同じ規則）。
    [Fact]
    public void fred指定でも鍵が無ければ未供給の実装へ倒す()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Fx:Provider", "fred"));

        configured.Services.GetRequiredService<IPeriodEndFxRateSource>()
            .Should().BeOfType<UnsuppliedPeriodEndFxRateSource>();
    }

    // **対の肯定形**: 判断サービスと同じ構成キーで実レート源（鮮度装飾つき）と期末レートのアダプタが組み上がる。
    [Fact]
    public void 為替レート源を設定すれば期末レートのアダプタが組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Fx:Provider", "fred");
            b.UseSetting("Fx:Fred:ApiKey", "key");
        });

        configured.Services.GetRequiredService<IFxRateSource>().Should().BeOfType<CachingFxRateSource>();
        configured.Services.GetRequiredService<IPeriodEndFxRateSource>()
            .Should().BeOfType<FxRateSourcePeriodEndFxRateSource>();
    }

    // レート源は singleton（TTL キャッシュを巡回で共有し、報告書の生成ごとに源を叩かない）。
    [Fact]
    public void 為替レート源はsingletonでキャッシュが共有される()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Fx:Provider", "fred");
            b.UseSetting("Fx:Fred:ApiKey", "key");
        });

        configured.Services.GetRequiredService<IFxRateSource>()
            .Should().BeSameAs(configured.Services.GetRequiredService<IFxRateSource>());
    }
}
