using AiStockTrading.MarketMonitor.Application.Adapters;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;
using AppSvc = AiStockTrading.MarketMonitor.Application.Services.MarketMonitorService;

namespace AiStockTrading.MarketMonitor.Application.Tests;

// FR-03, UC-02, ADR-0003, IADR-0014: 監視 1 巡回のオーケストレーション検証。
// 変動閾値・クールダウン・損切り検知・取得失敗スキップを固定する。
public class MarketMonitorServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredSymbol Aapl = new("AAPL", Market.UnitedStates);

    private static MarketMonitorSettings Settings(params MonitoredSymbol[] symbols) => new()
    {
        MovementThresholdRatio = 0.03m,
        Cooldown = TimeSpan.FromMinutes(15),
        MonitoredSymbols = symbols,
    };

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(Now);
        public FakeMarketDataSource Market { get; } = new();
        public InMemoryMonitoredSymbolStore Settings { get; }
        public InMemoryPositionStore Positions { get; } = new();
        public InMemoryPriceBaselineStore Baselines { get; } = new();
        public InMemoryCooldownStore Cooldowns { get; } = new();

        public Harness(MarketMonitorSettings settings)
        {
            Settings = new InMemoryMonitoredSymbolStore(settings);
        }

        public AppSvc Service() =>
            new(Settings, Positions, Baselines, Cooldowns, Market, Clock);
    }

    [Fact]
    public async Task 閾値超過かつクールダウン外なら価格変動イベントを生成しクールダウンを更新する()
    {
        var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m); // +4%

        var result = await h.Service().EvaluateRoundAsync();

        result.PriceMovements.Should().ContainSingle();
        result.PriceMovements[0].Symbol.Should().Be("AAPL");
        result.PriceMovements[0].BaselinePrice.Should().Be(1_000m);
        h.Cooldowns.GetLastTriggered("AAPL", Market.UnitedStates).Should().Be(Now);
    }

    [Fact]
    public async Task クールダウン中は閾値超過でも価格変動イベントを生成しない()
    {
        var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m);
        // 直近 5 分前にトリガー済み（クールダウン 15 分内）。
        h.Cooldowns.SetLastTriggered("AAPL", Market.UnitedStates, Now.AddMinutes(-5));

        var result = await h.Service().EvaluateRoundAsync();

        result.PriceMovements.Should().BeEmpty();
    }

    [Fact]
    public async Task クールダウン経過後は再び価格変動イベントを生成する()
    {
        var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m);
        h.Cooldowns.SetLastTriggered("AAPL", Market.UnitedStates, Now.AddMinutes(-20)); // 15 分経過

        var result = await h.Service().EvaluateRoundAsync();

        result.PriceMovements.Should().ContainSingle();
    }

    [Fact]
    public async Task クールダウンちょうど経過は再トリガーする()
    {
        // 境界: now - last == Cooldown（ちょうど 15 分）は「クールダウン外」（< 判定のため経過とみなす）。
        var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m);
        h.Cooldowns.SetLastTriggered("AAPL", Market.UnitedStates, Now.AddMinutes(-15)); // ちょうど 15 分

        (await h.Service().EvaluateRoundAsync()).PriceMovements.Should().ContainSingle();
    }

    [Fact]
    public async Task 閾値未満なら価格変動イベントを生成しない()
    {
        var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_020m); // +2%

        (await h.Service().EvaluateRoundAsync()).PriceMovements.Should().BeEmpty();
    }

    [Fact]
    public async Task 基準値が未確定なら価格変動イベントを生成しない()
    {
        var h = new Harness(Settings(Aapl));
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m); // baseline 未設定

        (await h.Service().EvaluateRoundAsync()).PriceMovements.Should().BeEmpty();
    }

    [Fact]
    public async Task 保有銘柄が損切りラインに到達したら損切りイベントを生成する()
    {
        var h = new Harness(Settings()); // 監視銘柄なし・保有のみ
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 965m); // 損切り価格 970 割れ

        var result = await h.Service().EvaluateRoundAsync();

        result.StopLosses.Should().ContainSingle();
        var sl = result.StopLosses[0];
        sl.Symbol.Should().Be("AAPL");
        sl.PositionSide.Should().Be(TradeSide.Buy);
        sl.Quantity.Should().Be(10);
        sl.StopLossPrice.Should().Be(970m);
    }

    [Fact]
    public async Task 損切りはクールダウンと独立に評価される()
    {
        // 同一銘柄がクールダウン中でも損切りは必ず評価する（フェイルセーフ）。
        var h = new Harness(Settings(Aapl));
        h.Cooldowns.SetLastTriggered("AAPL", Market.UnitedStates, Now.AddMinutes(-1));
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m);

        var result = await h.Service().EvaluateRoundAsync();

        result.StopLosses.Should().ContainSingle();
    }

    [Fact]
    public async Task 価格取得に失敗した銘柄はスキップし他を継続する()
    {
        var other = new MonitoredSymbol("MSFT", Market.UnitedStates);
        var h = new Harness(Settings(Aapl, other));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Baselines.SetBaseline("MSFT", Market.UnitedStates, 1_000m);
        // AAPL は価格未登録（取得失敗）、MSFT のみ +4%。
        h.Market.Set("MSFT", Market.UnitedStates, 1_040m);

        var result = await h.Service().EvaluateRoundAsync();

        result.PriceMovements.Should().ContainSingle(m => m.Symbol == "MSFT");
    }
}
