using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using AiStockTrading.Shared.Contracts.Llm;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// NFR, FR-06, MSP:ADR-0029, IADR-0284, IADR-0328, IADR-0332 決定 2, #746:
// **`LlmGateway:Grpc` の有無で輸送が切り替わり、既定は REST である**ことを配線ごと固定する。
//
// 🔴 観測点の取り方は取引判断側（`LlmGatewayGrpcWiringTests`・TradeDecisionService）と同じ ——
// `LlmGateway:BaseUrl` を与えずに `LlmGateway:Grpc` だけを与えたとき、REST 輸送は成立しない
// （プレースホルダ散文へ倒れる）ので、実 egress の実装が返れば gRPC 輸送が選ばれた証拠になる。
//
// 構成は `UseSetting`（ホスト構成）で与える。**`ConfigureAppConfiguration` では届かない**
// ——登録時に読む値だからである（IADR-0323 §影響・結果で実測済み）。
public class LlmGatewayGrpcWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    // 陽性: gRPC だけ設定 → 実 egress（プレースホルダではない）＋生成クライアントが登録される。
    [Fact]
    public void Grpc_だけ設定すれば_gRPC_輸送で実照会する()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting(LlmGatewayGrpc.AddressKey, "http://llmgateway-service:8081"));

        configured.Services.GetRequiredService<IReportNarrativeDrafter>()
            .Should().BeOfType<HttpReportNarrativeDrafter>();
        configured.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().NotBeNull();
    }

    // 陰性対照 1: 既定（どちらも無し）は安全既定のプレースホルダ散文のまま。
    [Fact]
    public void 既定は_gRPC_を配線しない()
    {
        using var configured = factory.WithWebHostBuilder(_ => { });

        configured.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().BeNull();
        configured.Services.GetRequiredService<IReportNarrativeDrafter>()
            .Should().BeOfType<PlaceholderReportNarrativeDrafter>();
    }

    // 陰性対照 2: REST だけ設定した従来のデプロイは**そのまま REST**（gRPC を勝手に使い始めない）。
    [Fact]
    public void BaseUrl_だけのデプロイは_REST_のまま()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting("LlmGateway:BaseUrl", "http://llm-gateway"));

        configured.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().BeNull();
        configured.Services.GetRequiredService<IReportNarrativeDrafter>()
            .Should().BeOfType<HttpReportNarrativeDrafter>();
    }

    // 陰性対照 3: 不正な値（scheme 無し）は gRPC を選ばず既定へ倒れる（起動は落とさない）。
    [Fact]
    public void 不正な_Grpc_の値は無視して既定へ倒れる()
    {
        using var configured = factory.WithWebHostBuilder(
            b => b.UseSetting(LlmGatewayGrpc.AddressKey, "llmgateway-service:8081"));

        configured.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().BeNull();
        configured.Services.GetRequiredService<IReportNarrativeDrafter>()
            .Should().BeOfType<PlaceholderReportNarrativeDrafter>();
    }
}
