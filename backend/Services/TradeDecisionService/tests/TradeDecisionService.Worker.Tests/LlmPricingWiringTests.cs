using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TradeDecision.Application.Ports;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// NFR（費用）, FR-04, #303, IADR-0122: モデル別単価（LlmPricing:PerModel:<model>:*）が実際に計上へ届くことを固定する。
// 配線が外れると「単価を入れたつもりで global 単一ペア（または 0）のまま」になり、症状が金額のズレなので気づきにくい。
public class LlmPricingWiringTests
{
    private static async Task<decimal> ReportAsync(Factory factory, string? model)
    {
        _ = factory.CreateClient();
        var harness = factory.Services.GetRequiredService<ITestHarness>();

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILlmUsageReporter>()
            .ReportAsync(new LlmUsage(1000, 2000, model));

        return harness.Published.Select<LlmCostIncurred>().Single().Context.Message.Amount;
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
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.PriceMovementDetectedConsumer>());
            });
        }
    }
}
