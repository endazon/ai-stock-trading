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

    // 🔴 T-10-2406, #1194, IADR-0505: 本番の組み立てで、定時サイクルのチェーンにだけ、再試行の連鎖が consumer_timeout に収まる試行の上限の
    // 失敗の規則が載る（共通の再試行 3 回より先に当たる規則）。既定・経路B（前提 12）は再試行しない（1 回目の失敗で _error へ）。
    [Theory]
    [InlineData(null, null, null, 1)]   // 既定: T 960 秒 → 600 ＋ 960 ＝ 1,560（2 回は 2,522）
    [InlineData(null, "12", null, 1)]   // 経路B: T 1,140 秒 → 1,740
    [InlineData("20", "4", null, 3)]    // T 340 秒 → 600 ＋ 3 × 340 ＋ 12 ＝ 1,632（4 回は 2,002）
    [InlineData(null, "12", "3600", 2)] // consumer_timeout を延ばすと再試行が戻る: 600 ＋ 2 × 1,140 ＋ 2 ＝ 2,882
    public void T_10_2406_定時サイクルのチェーンの試行の上限は再試行の連鎖から導かれる(
        string? llmTimeoutSeconds, string? maxWatchedSymbols, string? brokerConsumerTimeoutSeconds, int expectedAttempts)
    {
        using var factory = new Factory(llmTimeoutSeconds, maxWatchedSymbols, brokerConsumerTimeoutSeconds);
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<ScheduledCycleRetryChain>().Attempts.Should().Be(expectedAttempts);

        var chain = factory.Services.GetServices<ICodeFileCollection>().OfType<HandlerGraph>()
            .Select(g => g.ChainFor<InformationCollected>())
            .Should().ContainSingle(c => c != null).Which!;
        var rule = chain.Failures.Should().ContainSingle("定時サイクルのチェーンの規則は 1 つ").Which;
        rule.Match.Description.Should().Be("All exceptions");
        var slots = rule.ToList();
        slots.Select(s => s.Attempt).Should().Equal(Enumerable.Range(1, expectedAttempts));
        slots.Take(expectedAttempts - 1).Should().AllSatisfy(s => s.Describe().Should().StartWith("Retry inline"));
        slots[expectedAttempts - 1].Describe().Should().Be("Move to error queue", "試行の上限の回数目の失敗で _error へ送る");
    }

    // T-10-2406（否定形）: 試行の上限を掛けるのは定時サイクルだけ（価格変動の判断は共通の失敗方針のまま）。
    [Fact]
    public void T_10_2406_他のハンドラの失敗の規則は変えない()
    {
        using var factory = new Factory(null, null);
        _ = factory.CreateClient();

        var chain = factory.Services.GetServices<ICodeFileCollection>().OfType<HandlerGraph>()
            .Select(g => g.ChainFor<PriceMovementDetected>())
            .Should().ContainSingle(c => c != null).Which!;
        chain.Failures.Should().BeEmpty("価格変動の判断は共通の規則（再試行 3 回 → _error）のまま");
    }

    // 🔴 T-10-2404（否定形）: 試行 1 回でも consumer_timeout に収まらない構成（経路B で前提を 13 へ上げた: 600 ＋ 1,230 ＝ 1,830）は起動しない。
    // consumer_timeout を延ばした構成は起動する（検査が前提だけでなく consumer_timeout の構成値を読む）。
    [Theory]
    [InlineData(null, false)]
    [InlineData("3600", true)]
    public void T_10_2404_連鎖がconsumer_timeoutに収まらない構成は起動しない(string? brokerConsumerTimeoutSeconds, bool expectStarted)
    {
        using var factory = new Factory(null, "13", brokerConsumerTimeoutSeconds);

        var act = () => factory.CreateClient();

        if (expectStarted)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<Exception>()
                .Where(e => Flatten(e).Any(x => x is InvalidOperationException
                    && x.Message.Contains(ScheduledCycleRetryChain.BrokerConsumerTimeoutKey, StringComparison.Ordinal)));
        }
    }

    private static IEnumerable<Exception> Flatten(Exception e)
    {
        yield return e;
        if (e is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                yield return inner;
        }
        else if (e.InnerException is { } inner)
        {
            foreach (var nested in Flatten(inner))
                yield return nested;
        }
    }

    private sealed class Factory(string? llmTimeoutSeconds, string? maxWatchedSymbols, string? brokerConsumerTimeoutSeconds = null)
        : WebApplicationFactory<Program>
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
            if (brokerConsumerTimeoutSeconds is not null)
                builder.UseSetting(ScheduledCycleRetryChain.BrokerConsumerTimeoutKey, brokerConsumerTimeoutSeconds);

            // ADR-0013, IADR-0129: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
