using MarketMonitorService.Domain;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using AppSvc = MarketMonitorService.Features.MarketMonitor.MarketMonitorAppService;

namespace MarketMonitorService.Tests;

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

        // #909, IADR-0380 決定2: 既定は全市場が開場（従来の表明はそのまま）。市場別の閉場は個別テストが設定する。
        public FakeSchedule Schedule { get; } = new(open: true);

        public Harness(MarketMonitorSettings settings)
        {
            Settings = new InMemoryMonitoredSymbolStore(settings);
        }

        public AppSvc Service() =>
            new(Settings, Positions, Baselines, Cooldowns, Market, Schedule, Clock);
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

    [Fact]
    public async Task T_10_627_評価記録は価格の取れなかった保有も含み_到達判定は従来どおり()
    {
        // T-10-627, FR-10, #902, IADR-0365 決定1: 評価記録は観測の材料であり、到達の判定・発行は変えない。
        var h = new Harness(Settings());
        h.Positions.Set(
        [
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m),
            new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 2_000m, 1_900m),
            new HeldPosition("NVDA", Market.UnitedStates, TradeSide.Buy, 3, 150m, 140m), // 価格未登録（取得失敗）
        ]);
        h.Market.Set("AAPL", Market.UnitedStates, 340.12m); // 未到達
        h.Market.Set("MSFT", Market.UnitedStates, 1_850m); // 到達

        var result = await h.Service().EvaluateRoundAsync();

        result.StopLossEvaluations.Should().BeEquivalentTo(
        [
            new StopLossEvaluation("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 338.51m, 340.12m, Now),
            new StopLossEvaluation("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 1_900m, 1_850m, Now),
            new StopLossEvaluation("NVDA", Market.UnitedStates, TradeSide.Buy, 3, 140m, null, Now),
        ]);
        result.StopLosses.Should().ContainSingle().Which.Symbol.Should().Be("MSFT");
    }

    [Fact]
    public async Task T_10_695_閉場している市場の銘柄は照会も評価も到達もされず_保護の空白として残る()
    {
        // T-10-695, FR-03, FR-10, FR-01, #909, IADR-0380 決定2・決定3:
        // 閉場中の価格は終値で凍っており、そこで出した成行は翌寄りまで約定しない。**照会そのものを行わない。**
        var h = new Harness(Settings(Aapl, new MonitoredSymbol("7203", Market.Japan)));
        h.Schedule.ClosedMarkets.Add(Market.UnitedStates);
        h.Positions.Set(
        [
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m),
            new HeldPosition("7203", Market.Japan, TradeSide.Buy, 100, 3_000m, 2_900m),
        ]);
        h.Market.Set("AAPL", Market.UnitedStates, 330m); // 到達しているが閉場中なので出してはならない
        h.Market.Set("7203", Market.Japan, 2_800m);      // 開場中なので到達する
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Baselines.SetBaseline("7203", Market.Japan, 1_000m);

        var result = await h.Service().EvaluateRoundAsync();

        h.Market.Requested.Should().NotContain(("AAPL", Market.UnitedStates), "閉場中の市場は 1 回も照会しない");
        result.StopLosses.Should().ContainSingle().Which.Market.Should().Be(Market.Japan);
        result.PriceMovements.Should().OnlyContain(m => m.Market == Market.Japan);
        result.StopLossEvaluations.Should().OnlyContain(e => e.Market == Market.Japan);

        // 🔴 黙って飛ばさない: 閉場していた市場の保有は保護の空白として残り、価格は「照会していない」ので null。
        result.ClosedMarketPositions.Should().BeEquivalentTo(
        [
            new StopLossEvaluation("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 338.51m, null, Now),
        ]);
    }

    [Fact]
    public async Task T_10_695_開場している市場の銘柄は従来どおり照会され保護の空白に入らない()
    {
        // T-10-695（対の肯定形）, FR-03, #909, IADR-0380 決定2:
        // 全市場を閉場にする実装でも上のテストは緑になるため、開場側を別に固定する。
        var h = new Harness(Settings(Aapl));
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 330m);

        var result = await h.Service().EvaluateRoundAsync();

        h.Market.Requested.Should().Contain(("AAPL", Market.UnitedStates));
        result.StopLosses.Should().ContainSingle();
        result.ClosedMarketPositions.Should().BeEmpty();
    }

    // ---- 🔴 T-10-837, FR-03, FR-10, #957, IADR-0399 決定3: 市況の照会は銘柄ごとに閉じる ----
    //
    // 以前は 1 銘柄の照会の例外が巡回全体を落とし、全建玉の損切り検知・変動検知・生存要約が止まった
    // （実運用の市況源 Finnhub は銘柄 null で ArgumentNullException を投げ、FinnhubMarketDataSource は捕まえない。
    // HttpClient の上限による打ち切りは OperationCanceledException として再送出される）。

    public static TheoryData<string> QuoteFailures => ["ArgumentNullException", "呼び出し側以外の打ち切り", "InvalidOperationException"];

    private static Exception QuoteFailure(string kind) => kind switch
    {
        "ArgumentNullException" => new ArgumentNullException("stringToEscape"),
        "呼び出し側以外の打ち切り" => new TaskCanceledException("HttpClient.Timeout"),
        "InvalidOperationException" => new InvalidOperationException("市況源の不具合"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [MemberData(nameof(QuoteFailures))]
    public async Task T_10_837_1銘柄の照会の例外はその銘柄の価格欠落に閉じ_他の保有の到達と変動判定は続く(string kind)
    {
        var msft = new MonitoredSymbol("MSFT", Market.UnitedStates);
        var h = new Harness(Settings(Aapl, msft));
        h.Positions.Set(
        [
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m), // 照会が例外になる
            new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 2_000m, 1_900m),
        ]);
        h.Market.Set("MSFT", Market.UnitedStates, 1_850m); // ライン 1,900 を割っている
        h.Baselines.SetBaseline("MSFT", Market.UnitedStates, 2_000m); // -7.5%
        var log = new StopLossLivenessReporterTests.RecordingLogger<AppSvc>();
        var service = new AppSvc(
            h.Settings, h.Positions, h.Baselines, h.Cooldowns,
            new ThrowingForSymbol(h.Market, "AAPL", QuoteFailure(kind)), h.Schedule, h.Clock, log);

        var result = await service.EvaluateRoundAsync();

        result.StopLosses.Should().ContainSingle().Which.Symbol.Should().Be("MSFT", "健全な保有の到達は 1 銘柄の例外に巻き込まれない");
        result.PriceMovements.Should().ContainSingle().Which.Symbol.Should().Be("MSFT", "変動判定も続く");
        result.StopLossEvaluations.Should().BeEquivalentTo(
        [
            new StopLossEvaluation("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 338.51m, null, Now),
            new StopLossEvaluation("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 1_900m, 1_850m, Now),
        ], "照会できなかった保有は価格欠落として記録に残る（生存要約の欠落 Warning へ流れる）");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("AAPL/UnitedStates"));
    }

    [Fact]
    public async Task T_10_837_呼び出し側の停止要求は従来どおり伝わる()
    {
        var h = new Harness(Settings());
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m)]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = new AppSvc(
            h.Settings, h.Positions, h.Baselines, h.Cooldowns,
            new ThrowingForSymbol(h.Market, "AAPL", null), h.Schedule, h.Clock);

        var act = () => service.EvaluateRoundAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("監視の停止を「価格が取れない」と読み替えない");
    }

    // 指定の銘柄だけ照会で例外を投げる市況源（他の銘柄は inner に委ねる）。例外が null なら呼び出し側のトークンで打ち切る。
    private sealed class ThrowingForSymbol(IMarketDataSource inner, string symbol, Exception? failure) : IMarketDataSource
    {
        public Task<Quote?> GetLatestQuoteAsync(string requested, Market market, CancellationToken cancellationToken = default)
        {
            if (requested != symbol)
                return inner.GetLatestQuoteAsync(requested, market, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            throw failure ?? new InvalidOperationException("到達しない");
        }
    }
}
