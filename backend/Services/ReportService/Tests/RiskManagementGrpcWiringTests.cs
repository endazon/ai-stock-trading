using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Tests;

// T-10-1057, NFR, FR-06, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427 決定 5, #997 (#753):
// **本番の Program.cs の組み立て**で、`RiskManagement:Grpc` の有無が取引台帳の 6 つの供給元を切り替え、**既定は REST** である
// ことを固定する。
//
// 🔴 型を見るだけでなく、組み立てた実装で**実際に呼ぶ**（「配線を外しても全テストが緑」＝#947 の形を塞ぐ）。
// 構成は `UseSetting`（ホスト構成）で与える —— 登録時に読む値であり `ConfigureAppConfiguration` では届かない（段 1′ と同じ）。
public class RiskManagementGrpcWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    [Fact]
    public async Task T_10_1057_Grpc_を宣言すれば_6_つの供給元が_gRPC_実装になり実際に提供側を呼ぶ()
    {
        var uptime = new Proto.GetSessionUptimeResponse { Days = new Proto.SessionUptimeDays(), Stage1CumulativeCountedDays = 3 };
        var behavior = new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.Returns(new Proto.GetStageGateResponse { CurrentStage = Proto.TradingStage.Stage1Simulate }),
            SessionUptime = RiskReadStubBehavior.Returns(uptime),
            BuyInInferences = RiskReadStubBehavior.Returns(new Proto.GetBuyInInferencesResponse { PeriodCovered = true }),
        };
        await using var host = await RiskReadStubHost.StartAsync(behavior);
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RiskManagement:Grpc", host.Address);
            b.UseSetting("RiskManagement:BaseUrl", "http://risk-rest-must-not-be-used");
        });
        var sp = configured.Services;
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);

        sp.GetRequiredService<IPeriodFillSource>().Should().BeOfType<GrpcPeriodFillSource>("宣言があれば BaseUrl より gRPC を優先する");
        sp.GetRequiredService<IPeriodDriftAdoptionSource>().Should().BeOfType<GrpcPeriodDriftAdoptionSource>();
        sp.GetRequiredService<IBuyInInferenceRecordSource>().Should().BeOfType<GrpcBuyInInferenceRecordSource>();
        sp.GetRequiredService<IOpenPositionSource>().Should().BeOfType<GrpcOpenPositionSource>();
        sp.GetRequiredService<IOpenDUptimeSource>().Should().BeOfType<GrpcOpenDUptimeSource>();
        sp.GetRequiredService<IStageProgressSource>().Should().BeOfType<GrpcStageProgressSource>();

        (await sp.GetRequiredService<IPeriodFillSource>().GetFillsAsync(from, to)).Should().BeEmpty();
        (await sp.GetRequiredService<IPeriodDriftAdoptionSource>().GetDriftAdoptionsAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IBuyInInferenceRecordSource>().GetInferencesAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IOpenPositionSource>().GetOpenPositionsAsync()).Should().NotBeNull();
        (await sp.GetRequiredService<IOpenDUptimeSource>().GetUptimeAsync(from, to))!.Stage1CumulativeCountedDays.Should().Be(3);
        (await sp.GetRequiredService<IStageProgressSource>().GetCurrentStageAsync()).Should().NotBeNull();

        behavior.Calls.Should().Be(6, "組み立てた 6 つの実装が実際に偽の提供側を呼んだ");
    }

    // 陰性対照 1: 宣言が無ければ従来どおり REST（輸送そのものが登録されない）。
    [Fact]
    public void T_10_1057_宣言が無ければ_REST_のまま()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:BaseUrl", "http://risk"));
        var sp = configured.Services;

        sp.GetService<RiskManagementGrpcTransport>().Should().BeNull();
        sp.GetRequiredService<IPeriodFillSource>().Should().BeOfType<HttpPeriodFillSource>();
        sp.GetRequiredService<IPeriodDriftAdoptionSource>().Should().BeOfType<HttpPeriodDriftAdoptionSource>();
        sp.GetRequiredService<IBuyInInferenceRecordSource>().Should().BeOfType<HttpBuyInInferenceRecordSource>();
        sp.GetRequiredService<IOpenPositionSource>().Should().BeOfType<HttpOpenPositionSource>();
        sp.GetRequiredService<IOpenDUptimeSource>().Should().BeOfType<HttpOpenDUptimeSource>();
        sp.GetRequiredService<IStageProgressSource>().Should().BeOfType<HttpStageProgressSource>();
    }

    // 陰性対照 2: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない。段 1 と同じ）。
    [Theory]
    [InlineData("https://risk-management-service:8081")]
    [InlineData("risk-management-service:8081")]
    public void T_10_1057_使えない宛先は起動時に落とす(string address)
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("RiskManagement:Grpc", address));

        var act = () => configured.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*RiskManagement:Grpc*");
    }
}
