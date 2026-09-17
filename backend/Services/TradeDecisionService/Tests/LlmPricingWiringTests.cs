using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = TradeDecisionService.Infrastructure;

namespace TradeDecisionService.Tests;

// NFR（費用）, FR-04, #303, IADR-0122: モデル別単価（LlmPricing:PerModel:<model>:*）が実際に計上へ届くことを固定する。
// 配線が外れると「単価を入れたつもりで global 単一ペア（または 0）のまま」になり、症状が金額のズレなので気づきにくい。
public class LlmPricingWiringTests
{
    // ADR-0013, IADR-0129, #354: MassTransit の ITestHarness に代えて Wolverine.Tracking で発行を捕捉する。
    private static async Task<decimal> ReportAsync(Factory factory, string? model)
    {
        _ = factory.CreateClient();

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ILlmUsageReporter>()
                .ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 1000, 2000, model));
        });

        return session.Sent.MessagesOf<LlmCostIncurred>().Single().Amount;
    }

    // 基準1/2（#303）: 用途別割当で trade-decision=claude-sonnet-5。構成の単価がそのまま計上額になる。
    [Fact]
    public async Task モデル別単価が計上額に反映される()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["LlmPricing:PerModel:claude-sonnet-5:InputPer1kTokens"] = "0.327",
            ["LlmPricing:PerModel:claude-sonnet-5:OutputPer1kTokens"] = "1.637",
            ["LlmPricing:PerModel:claude-fable-5:InputPer1kTokens"] = "1.637",
            ["LlmPricing:PerModel:claude-fable-5:OutputPer1kTokens"] = "8.186",
        });

        (await ReportAsync(factory, "claude-sonnet-5")).Should().Be(3.601m);
    }

    // #817: env 名 `LlmPricing__PerModel__claude_sonnet_5__InputPer1kTokens`（シェル識別子）が構成キー
    // `LlmPricing:PerModel:claude_sonnet_5:*` になり、応答が名乗る `claude-sonnet-5` の計上額へ届く。
    [Fact]
    public async Task アンダースコア形のモデル別単価が計上額に反映される()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["LlmPricing:PerModel:claude_sonnet_5:InputPer1kTokens"] = "0.327",
            ["LlmPricing:PerModel:claude_sonnet_5:OutputPer1kTokens"] = "1.637",
            ["LlmPricing:PerModel:claude_fable_5:InputPer1kTokens"] = "1.637",
            ["LlmPricing:PerModel:claude_fable_5:OutputPer1kTokens"] = "8.186",
        });

        (await ReportAsync(factory, "claude-sonnet-5")).Should().Be(3.601m);
    }

    // #817 fail-loud: ゲートウェイが構成されているのに単価が実質 0（表が空・従来キーも無い）なら起動時に警告する。
    // 例外は投げない（IADR-0055: 0 は無害な fail-safe のまま。目的は無音で 0 円計上にしないこと）。
    [Fact]
    public void ゲートウェイ構成ありで単価が無ければ起動時に警告する()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["LlmGateway:BaseUrl"] = "http://llmgateway.invalid",
        }, logs);

        _ = factory.CreateClient();

        logs.Warnings.Should().Contain(m => m.Contains("LLM 単価が未設定"));
    }

    [Fact]
    public void 単価があれば起動時に警告しない()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["LlmGateway:BaseUrl"] = "http://llmgateway.invalid",
            ["LlmPricing:PerModel:claude_sonnet_5:InputPer1kTokens"] = "0.327",
            ["LlmPricing:PerModel:claude_sonnet_5:OutputPer1kTokens"] = "1.637",
        }, logs);

        _ = factory.CreateClient();

        logs.Warnings.Should().NotContain(m => m.Contains("LLM 単価が未設定"));
    }

    // ゲートウェイ未構成（プレースホルダ＝LLM を呼ばない）なら 0 円は実害が無いので警告しない（本番既定の静けさを保つ）。
    [Fact]
    public void ゲートウェイ未構成なら起動時に警告しない()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new Factory(logs: logs);

        _ = factory.CreateClient();

        logs.Warnings.Should().NotContain(m => m.Contains("LLM 単価が未設定"));
    }

    // 基準4（#303）: 表に無いモデルは最大単価（fable-5）へ倒れる＝過小計上を作らない。
    [Fact]
    public async Task 表に無いモデルは最大単価で計上される()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["LlmPricing:PerModel:claude-sonnet-5:InputPer1kTokens"] = "0.327",
            ["LlmPricing:PerModel:claude-sonnet-5:OutputPer1kTokens"] = "1.637",
            ["LlmPricing:PerModel:claude-fable-5:InputPer1kTokens"] = "1.637",
            ["LlmPricing:PerModel:claude-fable-5:OutputPer1kTokens"] = "8.186",
        });

        (await ReportAsync(factory, "claude-sonnet-4-6")).Should().Be(18.009m);
    }

    // 後方互換: PerModel を持たない既存デプロイは従来キー（global 単一ペア）のまま動く。
    [Fact]
    public async Task 従来キーだけの構成は従来どおり計上される()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["LlmPricing:InputPer1kTokens"] = "0.819",
            ["LlmPricing:OutputPer1kTokens"] = "4.093",
        });

        (await ReportAsync(factory, "claude-sonnet-5")).Should().Be(9.005m);
    }

    // 本番既定（values.yaml に単価を置かない・IADR-0114 決定6 / IADR-0122 決定4）は従来どおり ¥0 計上＝挙動不変。
    [Fact]
    public async Task 単価未設定なら_0_円で計上される()
    {
        using var factory = new Factory();

        (await ReportAsync(factory, "claude-sonnet-5")).Should().Be(0m);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings);

        public void Dispose() { }

        private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class Factory(IDictionary<string, string?>? settings = null, ILoggerProvider? logs = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 単価は Program.cs が登録時に構成を読むため、UseSetting（ホスト構成）で与える。
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                builder.UseSetting(key, value);

            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する
                // （ハンドラの発見は Program.cs 側の配線が担う）。
                services.DisableAllExternalWolverineTransports();

                // #817: Program.cs は AddSerilog で ILoggerFactory を差し替えるため、ConfigureLogging の provider には届かない。
                // 捕捉用の ILoggerFactory を後勝ちで登録する（app.Logger も ILoggerFactory から作られる）。
                if (logs is not null)
                    services.AddSingleton<ILoggerFactory>(new LoggerFactory([logs]));
            });
        }
    }
}
