using AiStockTrading.Configuration.Application;
using AiStockTrading.Configuration.Application.Adapters;
using AiStockTrading.Configuration.Application.Ports;
using AiStockTrading.Configuration.Application.Services;
using AiStockTrading.Configuration.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Configuration.Application.Tests;

// FR-17, UC-06, ADR-0007: 前提条件の更新（Version 増分・アクター/理由必須・前後値履歴・楽観排他）を検証する。
public class AssumptionsServiceTests
{
    private static AssumptionsService NewService(out InMemoryAssumptionsChangeLog log)
    {
        log = new InMemoryAssumptionsChangeLog();
        return new AssumptionsService(new InMemoryAssumptionsStore(), log, new FixedClock());
    }

    private static TradingAssumptions Modified() =>
        TradingAssumptionsDefaults.Create() with { CapitalGainsTaxRate = 0.15m };

    [Fact]
    public void 未設定時は既定値を_Version1_で返す()
    {
        var svc = NewService(out _);
        var current = svc.GetCurrent();
        current.Version.Should().Be(1);
        current.Assumptions.CapitalGainsTaxRate.Should().Be(0.20315m);
    }

    [Fact]
    public void 更新で_Version_が上がり履歴が前後値つきで記録される()
    {
        var svc = NewService(out var log);

        var newVersion = svc.Update(Modified(), expectedVersion: 1, actor: "owner", reason: "税率見直し");

        newVersion.Should().Be(2);
        svc.GetCurrent().Assumptions.CapitalGainsTaxRate.Should().Be(0.15m);

        var history = log.GetHistory();
        history.Should().ContainSingle();
        history[0].Actor.Should().Be("owner");
        history[0].Reason.Should().Be("税率見直し");
        history[0].Version.Should().Be(2);
        history[0].Before.Should().Contain("0.20315");
        history[0].After.Should().Contain("0.15");
    }

    [Theory]
    [InlineData("", "理由")]
    [InlineData("owner", "")]
    public void アクター_理由が空なら例外_更新されない(string actor, string reason)
    {
        var svc = NewService(out var log);

        var act = () => svc.Update(Modified(), 1, actor, reason);

        act.Should().Throw<ArgumentException>();
        log.GetHistory().Should().BeEmpty();
        svc.GetCurrent().Version.Should().Be(1);
    }

    [Fact]
    public void 版番号が不一致の更新は競合で弾かれる()
    {
        var svc = NewService(out var log);

        // 現在版は 1。古い版 0 で更新しようとする → 競合。
        var act = () => svc.Update(Modified(), expectedVersion: 0, actor: "owner", reason: "遅延更新");

        act.Should().Throw<AssumptionsConcurrencyException>();
        log.GetHistory().Should().BeEmpty();
        svc.GetCurrent().Version.Should().Be(1);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
    }
}
