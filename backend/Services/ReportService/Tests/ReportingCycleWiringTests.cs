using ReportService.Infrastructure.ExternalServices;
using ReportService.Features.Reports;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #338, #282, ADR-0016 決定15, ADR-0027, ADR-0017 決定2・決定4, IADR-0254:
// 報告サイクルの新しい供給（LLM 利用実績・借株料）が **composition root で実際に結線されている**ことを固定する。
//
// 🔴 **コンストラクタへ直接値を渡す単体テストでは、本番の配線が切れていても緑になる。**
// どちらのポートも安全既定（Unsupplied）を持つため、配線を落としても
// アダプタ単体のテストは通り、**本番だけが「未供給」を出し続ける**——
// #282 は「計上されていない費用が誰にも見えない」形の事故であり、これはその再発経路である。
// 外側から観測できる唯一の場所がここである（LlmGovernanceWiringTests と同じ役割）。
public class ReportingCycleWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    // 🔴 **否定形（安全既定）**: 監査台帳の URL が無い構成では未供給の実装へ倒す。
    // 空の記録（費用 0 円・借株コスト 0 USD）を返す実装へ倒してはならない。
    [Fact]
    public void 監査台帳が未設定なら未供給の実装へ倒す()
    {
        factory.Services.GetRequiredService<ILlmUsageRecordSource>()
            .Should().BeOfType<UnsuppliedLlmUsageRecordSource>();
        factory.Services.GetRequiredService<IBorrowFeeRecordSource>()
            .Should().BeOfType<UnsuppliedBorrowFeeRecordSource>();
    }

    // 🔴 **対の肯定形**: 監査台帳を設定した本番形では、**HTTP 実装が組み上がる**。
    // 否定形（未設定で倒れる）だけでは、HTTP 実装が一度も生成されなくても緑になる。
    [Fact]
    public void 監査台帳を設定すればHTTP実装が組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("Audit:BaseUrl", "http://audit"));

        configured.Services.GetRequiredService<ILlmUsageRecordSource>()
            .Should().NotBeOfType<UnsuppliedLlmUsageRecordSource>();
        configured.Services.GetRequiredService<IBorrowFeeRecordSource>()
            .Should().NotBeOfType<UnsuppliedBorrowFeeRecordSource>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    public void 監査台帳のURLが不正なら未供給の実装へ倒す(string baseUrl)
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Audit:BaseUrl", baseUrl));

        configured.Services.GetRequiredService<ILlmUsageRecordSource>()
            .Should().BeOfType<UnsuppliedLlmUsageRecordSource>();
        configured.Services.GetRequiredService<IBorrowFeeRecordSource>()
            .Should().BeOfType<UnsuppliedBorrowFeeRecordSource>();
    }

    // FR-15, ADR-0033 決定5, ADR-0037 決定3, #750: 見積り承認額の供給は**構成から読む唯一のポート**である。
    //
    // 🔴 **否定形**: 構成が無ければ null（未供給）——0 円へ倒さない。
    // 🔴 **対の肯定形**: 構成すれば同じポートがその値を返す（否定形だけでは、読み取りが常に null でも緑になる）。
    [Fact]
    public void 見積り承認額は構成が無ければ未供給であり構成すれば読める()
    {
        factory.Services.GetRequiredService<IStage0RecordingEstimateSource>()
            .Should().BeOfType<ConfigurationStage0RecordingEstimateSource>()
            .Which.GetApprovedEstimateJpy().Should().BeNull();

        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting(ConfigurationStage0RecordingEstimateSource.ConfigurationKey, "2000"));

        configured.Services.GetRequiredService<IStage0RecordingEstimateSource>()
            .GetApprovedEstimateJpy().Should().Be(2_000m);
    }

    // 🔴 **不正な構成は未供給へ倒す。0 円へ倒さない**（0 円で承認された、という別の主張になる）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public void 見積り承認額の構成が不正なら未供給へ倒す(string value)
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting(ConfigurationStage0RecordingEstimateSource.ConfigurationKey, value));

        configured.Services.GetRequiredService<IStage0RecordingEstimateSource>()
            .GetApprovedEstimateJpy().Should().BeNull();
    }

    // 🔴 **0 円の承認は未供給ではない**（構成に明示された値はそのまま出す）。
    [Fact]
    public void 見積り承認額のゼロ円は未供給ではない()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting(ConfigurationStage0RecordingEstimateSource.ConfigurationKey, "0"));

        configured.Services.GetRequiredService<IStage0RecordingEstimateSource>()
            .GetApprovedEstimateJpy().Should().Be(0m);
    }

    // 🔴 **ポートを登録しただけでは報告書に載らない。** 自動生成オーケストレータが
    // 両ポートを受け取れる形で組み上がることまで確かめる（受け取らなければ既定 null＝常に未供給になる）。
    [Fact]
    public void 自動生成オーケストレータが新しい供給を受け取って組み上がる()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("Audit:BaseUrl", "http://audit"));

        using var scope = configured.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ReportAutoGenerator>().Should().NotBeNull();
    }
}
