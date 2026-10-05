using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-02, NFR-02, #1169, IADR-0490 決定1: 本番の組み立て（Program.cs）で、定時サイクル（InformationCollected）のハンドラの
// 実行時間の上限が**構成から導いた値**になっていること（Wolverine の既定 60 秒のままではないこと）を固定する。
// ポリシーの登録を Program.cs から消すと、ここだけが赤くなる（ハンドラの試験は試験ホストで組むため緑のまま）。
public class ScheduledCycleTimeoutCompositionTests
{
    // T-10-2243: LLM の timeout・監視銘柄数の前提を変えると、チェーンの上限が導き直される。
    // 既定の判断の構成（一次スクリーニング有効・二次 1 票＝1 判断 2 回）で、上限 ＝ 前提 × (timeout × 2 ＋ 30) ＋ 60 秒。
    [Theory]
    [InlineData("20", "4", 340)]  // 4 × (20 × 2 + 30) + 60
    [InlineData("20", "7", 550)]  // 前提だけ変える
    [InlineData("45", "4", 540)]  // timeout だけ変える
    [InlineData(null, null, 960)] // どちらも未設定＝既定（30 秒・10 銘柄）
    public void T_10_2243_定時サイクルのハンドラの上限は構成から導かれる(
        string? llmTimeoutSeconds, string? maxWatchedSymbols, int expectedSeconds)
    {
        using var factory = new Factory(llmTimeoutSeconds, maxWatchedSymbols);
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<DecisionOrchestrationOptions>();
        options.EnableScreening.Should().BeTrue("前提: 既定の判断の構成は二段");
        options.VoteCount.Should().Be(1, "前提: 既定の判断の構成は 1 票");

        var budget = factory.Services.GetRequiredService<ScheduledCycleBudget>();
        budget.HandlerTimeoutSeconds.Should().Be(expectedSeconds);

        var chain = factory.Services.GetServices<ICodeFileCollection>().OfType<HandlerGraph>()
            .Select(g => g.ChainFor<InformationCollected>())
            .Should().ContainSingle(c => c != null).Which!;
        chain.ExecutionTimeoutInSeconds.Should().Be(expectedSeconds, "Wolverine の既定 60 秒に頼らない（#1169）");
    }

    // T-10-2243（否定形）: 上限を掛けるのは定時サイクルだけ（価格変動の判断など他のハンドラの既定を変えない）。
    [Fact]
    public void T_10_2243_他のハンドラの上限は変えない()
    {
        using var factory = new Factory(null, null);
        _ = factory.CreateClient();

        var chain = factory.Services.GetServices<ICodeFileCollection>().OfType<HandlerGraph>()
            .Select(g => g.ChainFor<PriceMovementDetected>())
            .Should().ContainSingle(c => c != null).Which!;
        chain.ExecutionTimeoutInSeconds.Should().BeNull("価格変動の判断は本件の範囲外（既定のまま）");
    }

    private sealed class Factory(string? llmTimeoutSeconds, string? maxWatchedSymbols) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            if (llmTimeoutSeconds is not null)
                builder.UseSetting("LlmGateway:TimeoutSeconds", llmTimeoutSeconds);
            if (maxWatchedSymbols is not null)
                builder.UseSetting(ScheduledCycleBudget.MaxWatchedSymbolsKey, maxWatchedSymbols);

            // ADR-0013, IADR-0129: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
