using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class Factory(IDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
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
            });
        }
    }
}
