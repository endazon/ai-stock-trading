using ReportService.Infrastructure.ExternalServices;
using ReportService.Features.Reports;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-15, FR-20, #569, IADR-0250, IADR-0271: 三者比較（現在段階）と OpenD 稼働率の供給が
// **composition root で実際に結線されている**ことを固定する。
//
// 🔴 **コンストラクタへ直接値を渡す単体テストでは、本番の配線が切れていても緑になる。**
// どちらのポートも安全既定（Unsupplied）を持つため、配線を落としてもアダプタ単体のテストは通り、
// **本番だけが「照会できませんでした」を出し続ける**（#563 が実際にその形で 1 度起きている）。
public class ThreeWayAndUptimeWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    // 🔴 **否定形（安全既定）**: リスク管理の URL が無い構成では未供給の実装へ倒す。
    // 空の記録（稼働 0 日）・Stage 0 を返す実装へ倒してはならない。
    [Fact]
    public void リスク管理が未設定なら未供給の実装へ倒す()
    {
        factory.Services.GetRequiredService<IOpenDUptimeSource>()
            .Should().BeOfType<UnsuppliedOpenDUptimeSource>();
        factory.Services.GetRequiredService<IStageProgressSource>()
            .Should().BeOfType<UnsuppliedStageProgressSource>();
    }

    // 🔴 **対の肯定形**: リスク管理を設定した本番形では **HTTP 実装が組み上がる**。
    [Fact]
    public void リスク管理を設定すればHTTP実装が組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("RiskManagement:BaseUrl", "http://risk-management"));

        configured.Services.GetRequiredService<IOpenDUptimeSource>()
            .Should().BeOfType<HttpOpenDUptimeSource>();
        configured.Services.GetRequiredService<IStageProgressSource>()
            .Should().BeOfType<HttpStageProgressSource>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    public void リスク管理のURLが不正なら未供給の実装へ倒す(string baseUrl)
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("RiskManagement:BaseUrl", baseUrl));

        configured.Services.GetRequiredService<IOpenDUptimeSource>()
            .Should().BeOfType<UnsuppliedOpenDUptimeSource>();
        configured.Services.GetRequiredService<IStageProgressSource>()
            .Should().BeOfType<UnsuppliedStageProgressSource>();
    }
}
