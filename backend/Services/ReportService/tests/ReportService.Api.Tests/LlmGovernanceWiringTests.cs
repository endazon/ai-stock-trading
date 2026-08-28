using AiStockTrading.Report.Application.Adapters;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Infrastructure.Composable.Adapters;
using AiStockTrading.Report.Infrastructure.Foundation.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Report.Api.Tests;

// FR-04, FR-06, FR-09, FR-16, NFR（費用）, ADR-0017 決定4, #335, #347, IADR-0217/0219:
// フォールバック発火の可視化（②通知・③台帳）と報告書生成費用の計上が、**composition root で
// 実際に結線されている**ことを固定する。
//
// 🔴 どちらのポートも既定は No-op（fail-safe）である。したがって配線を落としても
// アダプタ単体のテストは緑のままで、**本番だけが沈黙する**——その差を外側から観測できる唯一の場所がここである。
public class LlmGovernanceWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    // #347, IADR-0219: 費用計測は publish 実装が入る（No-op のままだと月報の実績が空になる＝#282 の再発）。
    [Fact]
    public void 費用計測ポートは発行実装へ結線される()
    {
        factory.Services.GetRequiredService<ILlmUsageReporter>()
            .Should().BeOfType<PublishingLlmUsageReporter>()
            .And.NotBeOfType<NoOpLlmUsageReporter>();
    }

    // #335, ADR-0017 決定4-(2)/(3): 発火の通知と台帳供給も publish 実装が入る。
    [Fact]
    public void 割当逸脱の可視化ポートは発行実装へ結線される()
    {
        factory.Services.GetRequiredService<ILlmGovernanceReporter>()
            .Should().BeOfType<PublishingLlmGovernanceReporter>()
            .And.NotBeOfType<NoOpLlmGovernanceReporter>();
    }

    // 🔴 **散文生成アダプタへ両ポートが渡っていなければ、発行実装があっても呼ばれない。**
    // ゲートウェイを設定した構成（本番形）で HTTP 実装が組み上がることを確かめる。
    [Fact]
    public void ゲートウェイ設定時の散文生成アダプタが組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("LlmGateway:BaseUrl", "http://llm-gateway"));

        configured.Services.GetRequiredService<IReportNarrativeDrafter>()
            .Should().BeOfType<HttpReportNarrativeDrafter>();
    }

    // 🔴 **否定形（安全既定）。** 未設定・不正 URI では定型散文へ倒す——
    // LLM へ繋がっていないのに繋がっているように見える構成を作らない。
    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    public void ゲートウェイ未設定または不正_URI_ならプレースホルダへ倒す(string baseUrl)
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("LlmGateway:BaseUrl", baseUrl));

        configured.Services.GetRequiredService<IReportNarrativeDrafter>()
            .Should().BeOfType<PlaceholderReportNarrativeDrafter>();
    }
}
