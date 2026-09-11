using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// NFR, FR-04, MSP:ADR-0029, IADR-0284, IADR-0328, IADR-0332 決定 2, #746:
// **`LlmGateway:Grpc` の有無で輸送が切り替わり、既定は REST である**ことを配線ごと固定する。
//
// 🔴 観測点の取り方: 判定器（`HttpLlmCompletionClient`）は輸送に依らず同じ型なので、型だけでは
// 区別できない。**`LlmGateway:BaseUrl` を与えずに `LlmGateway:Grpc` だけを与える**と、
// REST 輸送は成立しない（安全既定のプレースホルダへ倒れる）——それでも実 egress の実装が返るなら、
// gRPC 輸送が選ばれた証拠になる。あわせて生成クライアントの DI 登録の有無を見る。
//
// 構成は `UseSetting`（ホスト構成）で与える。**`ConfigureAppConfiguration` では届かない**
// ——登録時に読む値だからである（IADR-0323 §影響・結果で実測済み）。
public class LlmGatewayGrpcWiringTests
{
    // 陽性: gRPC だけ設定 → 実 egress（プレースホルダではない）＋生成クライアントが登録される。
    [Fact]
    public void Grpc_だけ設定すれば_gRPC_輸送で実照会する()
    {
        using var factory = new Factory(grpc: "http://llmgateway-service:8081");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>()
            .Should().BeOfType<HttpLlmCompletionClient>();
        factory.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().NotBeNull();
    }

    // 陰性対照 1: 既定（どちらも無し）は安全既定のプレースホルダのまま。生成クライアントも登録されない。
    [Fact]
    public void 既定は_gRPC_を配線しない()
    {
        using var factory = new Factory(grpc: null);
        _ = factory.CreateClient();

        factory.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().BeNull();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>()
            .Should().BeOfType<PlaceholderLlmCompletionClient>();
    }

    // 陰性対照 2: REST だけ設定した従来のデプロイは**そのまま REST**（gRPC を勝手に使い始めない）。
    [Fact]
    public void BaseUrl_だけのデプロイは_REST_のまま()
    {
        using var factory = new Factory(grpc: null, baseUrl: "http://llm-gateway");
        _ = factory.CreateClient();

        factory.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().BeNull();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>()
            .Should().BeOfType<HttpLlmCompletionClient>();
    }

    // 陰性対照 3: 不正な値（scheme 無し）は gRPC を選ばず、REST の既定へ倒れる（起動は落とさない）。
    [Fact]
    public void 不正な_Grpc_の値は無視して既定へ倒れる()
    {
        using var factory = new Factory(grpc: "llmgateway-service:8081");
        _ = factory.CreateClient();

        factory.Services.GetService<LlmCompletion.LlmCompletionClient>().Should().BeNull();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>()
            .Should().BeOfType<PlaceholderLlmCompletionClient>();
    }

    // 🔴 チャネルはプロセスに 1 本（`ILlmCompletionClient` は Scoped。解決のたびに作ると接続が増え続ける）。
    [Fact]
    public void 生成クライアントはプロセスに_1_つだけ作られる()
    {
        using var factory = new Factory(grpc: "http://llmgateway-service:8081");
        _ = factory.CreateClient();

        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        first.ServiceProvider.GetRequiredService<LlmCompletion.LlmCompletionClient>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<LlmCompletion.LlmCompletionClient>());
    }

    private sealed class Factory(string? grpc, string? baseUrl = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
            }));

            // 登録時に読む値は UseSetting（ホスト構成）で与える。
            if (grpc is not null)
                builder.UseSetting("LlmGateway:Grpc", grpc);
            if (baseUrl is not null)
                builder.UseSetting("LlmGateway:BaseUrl", baseUrl);

            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
