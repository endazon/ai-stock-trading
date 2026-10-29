using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using ConfigurationService.Common.Abstractions;
using ConfigurationService.Features.Assumptions;
using ConfigurationService.Infrastructure.Persistence;
using Xunit;

namespace ConfigurationService.Tests;

// 🔴 SC-01, FR-17, 05_trading-assumptions §6, IADR-0605, #1514（#1410 の網羅表 A-023）:
// 月次費用上限は「総額 ＝ LLM＋インフラ＋データ」（§6 が総額を内訳の合計として定義する）。各欄は 0 以上（データ 0 が既定）。
// **後段が正（権威）**: 矛盾する値・負の欄は 400（利用者へ見せる理由つき）で拒否し、保存も履歴も残さない。読み取りは変えない。
public class CostLimitsConsistencyTests
{
    private static AssumptionsService NewService(out InMemoryAssumptionsChangeLog log)
    {
        log = new InMemoryAssumptionsChangeLog();
        return new AssumptionsService(new InMemoryAssumptionsStore(), log, new FixedClock());
    }

    private static TradingAssumptions With(MonthlyCostLimits limits) =>
        TradingAssumptionsDefaults.Create() with { CostLimits = limits };

    // 🔴 既定値（20,000 ＝ 15,000＋5,000＋0）は規則を満たす。満たさないと、配備の直後に SC-01 で何も保存できなくなる。
    [Fact]
    public void 既定値は内訳の合計と総額が一致し全欄が0以上()
    {
        var limits = TradingAssumptionsDefaults.Create().CostLimits;

        limits.Should().Be(new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m));
        CostLimitsConsistency.Violation(limits).Should().BeNull();
    }

    [Theory]
    [InlineData(20_000, 15_000, 5_000, 0)]
    [InlineData(50_000, 30_000, 15_000, 5_000)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(15_000.5, 10_000.25, 5_000.25, 0)]
    public void 合計が総額と一致し全欄が0以上なら保存できる(decimal total, decimal llm, decimal infra, decimal data)
    {
        var svc = NewService(out var log);

        var version = svc.Update(With(new MonthlyCostLimits(total, llm, infra, data)), expectedVersion: 1, actor: "owner", reason: "費用上限の見直し");

        version.Should().Be(2);
        svc.GetCurrent().Assumptions.CostLimits.Should().Be(new MonthlyCostLimits(total, llm, infra, data));
        log.GetHistory().Should().ContainSingle();
    }

    [Theory]
    [InlineData(25_000, 15_000, 5_000, 0, "一致しません")] // 合計 20,000 < 総額
    [InlineData(20_000, 15_000, 5_000, 1_000, "一致しません")] // 合計 21,000 > 総額
    [InlineData(20_000, 25_000, -5_000, 0, "0 以上")] // 合計は一致するが負の欄がある
    [InlineData(-1, 0, 0, -1, "0 以上")]
    public void 合計が総額と一致しないか負の欄があれば拒否し保存も履歴も残さない(
        decimal total, decimal llm, decimal infra, decimal data, string expected)
    {
        var svc = NewService(out var log);

        var act = () => svc.Update(With(new MonthlyCostLimits(total, llm, infra, data)), expectedVersion: 1, actor: "owner", reason: "費用上限の見直し");

        act.Should().Throw<ArgumentException>().WithMessage($"*{expected}*");
        svc.GetCurrent().Version.Should().Be(1);
        svc.GetCurrent().Assumptions.CostLimits.Should().Be(TradingAssumptionsDefaults.Create().CostLimits);
        log.GetHistory().Should().BeEmpty();
    }

    // 本物の Program.cs: 400 の本文に理由（利用者へ見せる印つき）が載る。
    [Fact]
    public async Task PUT_で合計が総額と一致しなければ_400_で理由を返す()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");

        var put = await client.PutAsJsonAsync("/assumptions", new
        {
            assumptions = With(new MonthlyCostLimits(30_000m, 15_000m, 5_000m, 0m)),
            expectedVersion = 1,
            reason = "費用上限の見直し",
        }, TestContext.Current.CancellationToken);

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken);
        body!["error"]!.GetValue<string>().Should().Contain("一致しません").And.Contain("30000").And.Contain("20000");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    }
}
