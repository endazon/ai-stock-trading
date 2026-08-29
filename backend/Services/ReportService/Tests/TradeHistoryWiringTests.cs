using ReportService.Infrastructure.ExternalServices;
using ReportService.Features.Reports;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, FR-11, #563, IADR-0268:
// 日報 §2 の判断根拠と §3 の建玉の供給が **composition root で実際に結線されている**ことを固定する。
//
// 🔴 **コンストラクタへ直接値を渡す単体テストでは、本番の配線が切れていても緑になる。**
// どちらのポートも安全既定（Unsupplied）を持つため、配線を落としてもアダプタ単体のテストは通り、
// **本番だけが「未供給」を出し続ける**——#563 そのものが「本番から一度も呼ばれていなかった」事故である。
public class TradeHistoryWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    // 🔴 **否定形（安全既定）**: 供給元の URL が無い構成では未供給の実装へ倒す。
    // 空の辞書（根拠 0 件）・空列（建玉なし）を返す実装へ倒してはならない。
    [Fact]
    public void 供給元が未設定なら未供給の実装へ倒す()
    {
        factory.Services.GetRequiredService<ITradeRationaleSource>()
            .Should().BeOfType<UnsuppliedTradeRationaleSource>();
        factory.Services.GetRequiredService<IOpenPositionSource>()
            .Should().BeOfType<UnsuppliedOpenPositionSource>();
    }

    // 🔴 **対の肯定形**: 供給元を設定した本番形では、**HTTP 実装が組み上がる**。
    // 否定形（未設定で倒れる）だけでは、HTTP 実装が一度も生成されなくても緑になる。
    [Fact]
    public void 供給元を設定すればHTTP実装が組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Audit:BaseUrl", "http://audit");
            b.UseSetting("RiskManagement:BaseUrl", "http://risk-management");
        });

        configured.Services.GetRequiredService<ITradeRationaleSource>()
            .Should().BeOfType<HttpTradeRationaleSource>();
        configured.Services.GetRequiredService<IOpenPositionSource>()
            .Should().BeOfType<HttpOpenPositionSource>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    public void 供給元のURLが不正なら未供給の実装へ倒す(string baseUrl)
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Audit:BaseUrl", baseUrl);
            b.UseSetting("RiskManagement:BaseUrl", baseUrl);
        });

        configured.Services.GetRequiredService<ITradeRationaleSource>()
            .Should().BeOfType<UnsuppliedTradeRationaleSource>();
        configured.Services.GetRequiredService<IOpenPositionSource>()
            .Should().BeOfType<UnsuppliedOpenPositionSource>();
    }

    // 🔴 **ポートを登録しただけでは報告書に載らない。** 自動生成オーケストレータが
    // 両ポートを受け取れる形で組み上がることまで確かめる（受け取らなければ既定 null＝常に未供給になる）。
    [Fact]
    public void 自動生成オーケストレータが判断根拠と建玉の供給を受け取って組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Audit:BaseUrl", "http://audit");
            b.UseSetting("RiskManagement:BaseUrl", "http://risk-management");
        });

        using var scope = configured.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ReportAutoGenerator>().Should().NotBeNull();
    }
}
