using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;

namespace ReportService.Tests;

// T-10-1674, NFR, FR-06, FR-11, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445 決定 5, #1059 (#753):
// **本番の Program.cs の組み立て**で、`Audit:Grpc` の有無が監査台帳の 6 つの供給元を切り替え、**既定は REST** であることを固定する。
//
// 🔴 型を見るだけでなく、組み立てた実装で**実際に呼ぶ**（「配線を外しても全テストが緑」＝#947 の形を塞ぐ）。
// 構成は `UseSetting`（ホスト構成）で与える —— 登録時に読む値であり `ConfigureAppConfiguration` では届かない（段 2 と同じ）。
public class AuditGrpcWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    [Fact]
    public async Task T_10_1674_Grpc_を宣言すれば_6_つの供給元が_gRPC_実装になり実際に提供側を呼ぶ()
    {
        await using var host = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior());
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Audit:Grpc", host.Address);
            b.UseSetting("Audit:BaseUrl", "http://audit-rest-must-not-be-used");
        });
        var sp = configured.Services;
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);

        sp.GetRequiredService<IFxSourceStatusSource>().Should().BeOfType<GrpcFxSourceStatusSource>("宣言があれば BaseUrl より gRPC を優先する");
        sp.GetRequiredService<ILlmUsageRecordSource>().Should().BeOfType<GrpcLlmUsageRecordSource>();
        sp.GetRequiredService<IBorrowFeeRecordSource>().Should().BeOfType<GrpcBorrowFeeRecordSource>();
        sp.GetRequiredService<IStopLossMethodUsageSource>().Should().BeOfType<GrpcStopLossMethodUsageSource>();
        sp.GetRequiredService<IStopLossMethodResolutionSource>().Should().BeOfType<GrpcStopLossMethodResolutionSource>();
        sp.GetRequiredService<ITradeRationaleSource>().Should().BeOfType<GrpcTradeRationaleSource>();

        (await sp.GetRequiredService<IFxSourceStatusSource>().GetStatusAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<ILlmUsageRecordSource>().GetUsageAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IBorrowFeeRecordSource>().GetBorrowFeesAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IStopLossMethodUsageSource>().GetUsageAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<IStopLossMethodResolutionSource>().GetResolutionsAsync(from, to)).Should().NotBeNull();
        (await sp.GetRequiredService<ITradeRationaleSource>().GetRationalesAsync(from, to)).Should().NotBeNull();

        host.Behavior.Calls.Should().Be(6, "組み立てた 6 つの実装が実際に偽の提供側を呼んだ");
    }

    // 陰性対照 1: 宣言が無ければ従来どおり REST（輸送そのものが登録されない）。
    [Fact]
    public void T_10_1674_宣言が無ければ_REST_のまま()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Audit:BaseUrl", "http://audit"));
        var sp = configured.Services;

        sp.GetService<AuditGrpcTransport>().Should().BeNull();
        sp.GetRequiredService<IFxSourceStatusSource>().Should().BeOfType<HttpFxSourceStatusSource>();
        sp.GetRequiredService<ILlmUsageRecordSource>().Should().BeOfType<HttpLlmUsageRecordSource>();
        sp.GetRequiredService<IBorrowFeeRecordSource>().Should().BeOfType<HttpBorrowFeeRecordSource>();
        sp.GetRequiredService<IStopLossMethodUsageSource>().Should().BeOfType<HttpStopLossMethodUsageSource>();
        sp.GetRequiredService<IStopLossMethodResolutionSource>().Should().BeOfType<HttpStopLossMethodResolutionSource>();
        sp.GetRequiredService<ITradeRationaleSource>().Should().BeOfType<HttpTradeRationaleSource>();
    }

    // 陰性対照 2: 段 2 の宛先（リスク管理）だけを宣言しても監査台帳は REST のまま（輸送が混ざらない）。
    [Fact]
    public void T_10_1674_リスク管理の宛先だけでは監査台帳は_gRPC_にならない()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RiskManagement:Grpc", "http://risk-management-service:8081");
            b.UseSetting("Audit:BaseUrl", "http://audit");
        });
        var sp = configured.Services;

        sp.GetService<AuditGrpcTransport>().Should().BeNull();
        sp.GetRequiredService<ITradeRationaleSource>().Should().BeOfType<HttpTradeRationaleSource>();
    }

    // 陰性対照 3: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない。段 1・段 2 と同じ）。
    [Theory]
    [InlineData("https://audit-service:8081")]
    [InlineData("audit-service:8081")]
    public void T_10_1674_使えない宛先は起動時に落とす(string address)
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Audit:Grpc", address));

        var act = () => configured.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*Audit:Grpc*");
    }
}
