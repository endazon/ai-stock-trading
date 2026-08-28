using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-05, IADR-0018: 取引台帳から PortfolioState を組み立てる純射影の検証。
public class PortfolioProjectionTests
{
    private const decimal InitialCapital = 100_000m;

    // #337（#249 吸収）, IADR-0246: 当日判定は「判定時点（now）」と「約定時刻」を**同じ市場の現地取引日**へ
    // 写して比較する。判定時点は既存テストの当日正午（JST）に固定する（相対的な当日/前日関係は市場に
    // よらず保存される——両者を同じ TZ で写すため）。
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.FromHours(9));

    // JST 当日の約定時刻（+9 の 12:00 = UTC 03:00）。
    private static DateTimeOffset TodayAt(int hour = 12) =>
        new(2026, 7, 10, hour, 0, 0, TimeSpan.FromHours(9));

    private static DateTimeOffset OnDay(int day, int hour = 12) =>
        new(2026, 7, day, hour, 0, 0, TimeSpan.FromHours(9));

    private static LedgerFill Fill(
        TradeSide side, PositionEffect effect, int qty, decimal price,
        DateTimeOffset at, string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(symbol, market, side, effect, qty, price, at);

    [Fact]
    public void 買い建ては建玉_取得額_保有数_当日取引銘柄に反映される()
    {
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt()) },
            Now, InitialCapital);

        state.OpenPositionCount.Should().Be(1);
        state.InvestedCapital.Should().Be(10_000m);
        state.DailyOrderedAmount.Should().Be(10_000m);
        state.SymbolsTradedToday.Should().Contain(("AAPL", Market.UnitedStates));
        state.DailyRealizedPnl.Should().Be(0m);
        state.Capital.Should().Be(InitialCapital);
    }

    [Fact]
    public void 一部決済は平均取得単価で実現損益を計上し残建玉が残る()
    {
        // 10 株 @1,000 で建て、6 株を @1,200 で決済 → 実現 (1,200-1,000)*6 = +1,200。残 4 株 @1,000。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 6, 1_200m, TodayAt(10)),
            },
            Now, InitialCapital);

        state.DailyRealizedPnl.Should().Be(1_200m);
        state.OpenPositionCount.Should().Be(1);
        state.InvestedCapital.Should().Be(4_000m);
    }

    [Fact]
    public void 全決済で建玉はゼロになり保有数と取得額がゼロになる()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 900m, TodayAt(10)),
            },
            Now, InitialCapital);

        state.OpenPositionCount.Should().Be(0);
        state.InvestedCapital.Should().Be(0m);
        state.DailyRealizedPnl.Should().Be(-1_000m); // (900-1000)*10
    }

    [Fact]
    public void ショート建ては値下がりで利益になる()
    {
        // Sell 建て 10 @1,000、Buy で決済 @800 → 実現 (1,000-800)*10 = +2,000。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Sell, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Buy, PositionEffect.Close, 10, 800m, TodayAt(10)),
            },
            Now, InitialCapital);

        state.DailyRealizedPnl.Should().Be(2_000m);
        state.OpenPositionCount.Should().Be(0);
    }

    [Fact]
    public void 資金は当日より前の実現損益を反映し当日実現は含めない()
    {
        // 前日: +3,000 の実現。当日: -500 の実現。当日開始資金 = 100,000 + 3,000。当日実現は Capital に含めない。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, OnDay(9, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_300m, OnDay(9, 10)), // 前日 +3,000
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 950m, TodayAt(10)),    // 当日 -500
            },
            Now, InitialCapital);

        state.Capital.Should().Be(103_000m);
        state.DailyRealizedPnl.Should().Be(-500m);
    }

    [Fact]
    public void 連敗は連続する損失決済を数え利益決済でリセットする()
    {
        // 損, 損, 益, 損 の順 → 直近から遡って連続損失は 1。
        var state = PortfolioProjection.Project(
            new[]
            {
                // 損 (-100)
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(6, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(6, 10)),
                // 損 (-100)
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(7, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(7, 10)),
                // 益 (+100) → リセット
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(8, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 1_100m, OnDay(8, 10)),
                // 損 (-100)
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(9, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(9, 10)),
            },
            Now, InitialCapital);

        state.ConsecutiveLosses.Should().Be(1);
    }

    [Fact]
    public void 連敗は複数の連続損失を積み上げる()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(7, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 900m, OnDay(7, 10)),
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(8, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 800m, OnDay(8, 10)),
                Fill(TradeSide.Buy, PositionEffect.Open, 1, 1_000m, OnDay(9, 9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 1, 850m, OnDay(9, 10)),
            },
            Now, InitialCapital);

        state.ConsecutiveLosses.Should().Be(3);
    }

    [Fact]
    public void 当日発注金額は当日約定代金の合計になる()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),   // 10,000
                Fill(TradeSide.Buy, PositionEffect.Open, 5, 2_000m, TodayAt(10), symbol: "MSFT"), // 10,000
                Fill(TradeSide.Buy, PositionEffect.Open, 3, 1_000m, OnDay(9, 10), symbol: "GOOG"), // 前日 → 発注額に含めない
            },
            Now, InitialCapital);

        state.DailyOrderedAmount.Should().Be(20_000m);
        state.OpenPositionCount.Should().Be(3); // AAPL, MSFT, GOOG
    }

    // FR-10, #302, #329, IADR-0130 決定4（否定形）: 日次発注枠のカウンタは**新規建てだけ**を積む。
    // 計画 §5「新規建ての発注代金の合計で判定し、手仕舞い（決済）注文は算入しない」。
    // ゲート（RiskEvaluator）だけを直してカウンタを直さないと「拒否はされないが枠は減る」状態が残り、
    // 大口決済が当日の新規建て枠を枯渇させて手仕舞いをためらわせる（ADR-0009 と逆向きの誘因）。
    [Fact]
    public void 決済の約定は当日発注累計を消費しない()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),    // 新規建て 10,000
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 5_000m, TodayAt(10)), // 決済 50,000 は算入しない
            },
            Now, InitialCapital);

        state.DailyOrderedAmount.Should().Be(10_000m);
    }

    // FR-10, #302（否定形）: 決済しか無い日は当日発注累計が 0 のままである（枠が丸ごと残る）。
    [Fact]
    public void 決済だけの日は当日発注累計がゼロのままである()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, OnDay(9, 10)),  // 前日の新規建て
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 5_000m, TodayAt(10)), // 当日は決済のみ
            },
            Now, InitialCapital);

        state.DailyOrderedAmount.Should().Be(0m);
        // 決済そのものは当日取引銘柄・実現損益には反映される（差金決済防止・日次損失の入力）。
        state.SymbolsTradedToday.Should().Contain(("AAPL", Market.UnitedStates));
        state.DailyRealizedPnl.Should().Be(40_000m);
    }

    // FR-10, #302, #329 第 3 段階（否定形）: **決済に見せかけた新規建てで日次枠を逃れられない**。
    // 日次枠は新規建て（Open）のみを算入するため、「決済」と称した約定は枠を消費しない。在庫を超えて
    // 反転した分は**新しい建玉**であるが、それも投入額（段階資金上限の入力）と保有建玉数へ計上されるため、
    // 露出そのものは統制の視界から消えない。
    //
    // なお建玉効果は上流（TradeDecision の PositionEffectResolver・IADR-0119）が**保有建玉から**決めており、
    // 保有なし・不明の売りは Close にならず見送られる（AI が「決済」と申告して統制を外す経路は無い）。
    [Fact]
    public void 決済に見せかけて在庫を超えた約定でも残る建玉は投入額と建玉数に計上される()
    {
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),    // ロング 10 株
                Fill(TradeSide.Sell, PositionEffect.Close, 30, 1_000m, TodayAt(10)), // 「決済」30 株＝20 株の反転
            },
            Now, InitialCapital);

        state.DailyOrderedAmount.Should().Be(10_000m, "決済（Close）は当日発注累計を消費しない");
        state.OpenPositionCount.Should().Be(1, "反転して残ったショート建玉は保有建玉数に数える");
        state.InvestedCapital.Should().Be(20_000m, "残ったショート建玉の取得額は段階資金上限の累計に載る");
    }

    [Fact]
    public void 空の台帳は初期資金のみの状態を返す()
    {
        var state = PortfolioProjection.Project(Array.Empty<LedgerFill>(), Now, InitialCapital);

        state.Capital.Should().Be(InitialCapital);
        state.OpenPositionCount.Should().Be(0);
        state.InvestedCapital.Should().Be(0m);
        state.DailyRealizedPnl.Should().Be(0m);
        state.DailyOrderedAmount.Should().Be(0m);
        state.UnrealizedPnl.Should().Be(0m);
        state.DrawdownRatio.Should().Be(0m);
        state.ConsecutiveLosses.Should().Be(0);
        state.SymbolsTradedToday.Should().BeEmpty();
    }

    [Fact]
    public void 建て増しは加重平均で取得単価を更新する()
    {
        // 10 @1,000 と 10 @1,400 → 平均 1,200。20 株保有、取得額 24,000。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_400m, TodayAt(10)),
            },
            Now, InitialCapital);

        state.InvestedCapital.Should().Be(24_000m);
        state.OpenPositionCount.Should().Be(1);
        state.DailyRealizedPnl.Should().Be(0m);
    }

    // FR-03, FR-10, IADR-0030: 保有ポジションの射影（損切りライン検知への供給）。
    [Fact]
    public void 保有射影は銘柄別ネット建玉を平均取得単価で返す()
    {
        var goog = "GOOG";
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_400m, TodayAt(10)), // AAPL 平均 1,200・20株
                Fill(TradeSide.Buy, PositionEffect.Open, 5, 2_000m, TodayAt(11), symbol: goog),
            });

        positions.Should().HaveCount(2);
        var aapl = positions.Single(p => p.Symbol == "AAPL");
        aapl.Side.Should().Be(TradeSide.Buy);
        aapl.Quantity.Should().Be(20);
        aapl.AverageEntryPrice.Should().Be(1_200m);
        positions.Single(p => p.Symbol == goog).Quantity.Should().Be(5);
    }

    [Fact]
    public void 保有射影は全決済済み銘柄を除外する()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_100m, TodayAt(10)), // 全決済
            });

        positions.Should().BeEmpty();
    }

    // FR-03/04/10, IADR-0035: 損切り価格（最新の同方向エントリー・一部決済で保持・反転で更新・欠損は null）。
    private static LedgerFill FillSl(TradeSide side, int qty, decimal price, decimal? stop, int hour, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, TodayAt(hour), stop);

    [Fact]
    public void 保有射影の損切りは最新の建て増しエントリーを採る()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                FillSl(TradeSide.Buy, 10, 1_000m, 970m, 9),
                FillSl(TradeSide.Buy, 10, 1_400m, 1_358m, 10), // 建て増し → 最新の損切りに更新
            });

        positions.Single().StopLossPrice.Should().Be(1_358m);
    }

    [Fact]
    public void 保有射影の損切りは一部決済で保持される()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                FillSl(TradeSide.Buy, 10, 1_000m, 970m, 9),
                FillSl(TradeSide.Sell, 4, 1_100m, null, 10), // 一部決済（反対方向）→ 損切りは保持
            });

        positions.Single().Quantity.Should().Be(6);
        positions.Single().StopLossPrice.Should().Be(970m);
    }

    [Fact]
    public void 保有射影の損切りは反転で反転約定のエントリーに更新される()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[]
            {
                FillSl(TradeSide.Buy, 5, 1_000m, 970m, 9),
                FillSl(TradeSide.Sell, 8, 1_100m, 1_133m, 10), // 反転 → 新ショートの損切り
            });

        var p = positions.Single();
        p.Side.Should().Be(TradeSide.Sell);
        p.StopLossPrice.Should().Be(1_133m);
    }

    [Fact]
    public void 保有射影の損切りは欠損なら_null()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[] { FillSl(TradeSide.Buy, 10, 1_000m, null, 9) });

        positions.Single().StopLossPrice.Should().BeNull();
    }

    // FR-10, IADR-0036: 現在値・ピーク入力による含み損益・DD の時価算出。
    [Fact]
    public void 現在値とピークを与えると含み損益とDDを時価で算出する()
    {
        // AAPL 10 @1,000 を当日建て。現在値 1,100 → 含み +1,000。Capital=100,000・DailyRealizedPnl=0。
        // 現在エクイティ = 100,000 + 0 + 1,000 = 101,000。ピーク 105,000 → DD = (105,000−101,000)/105,000。
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)) },
            Now, InitialCapital,
            currentPrices: new Dictionary<(string, Market), decimal> { [("AAPL", Market.UnitedStates)] = 1_100m },
            equityHighWaterMark: 105_000m);

        state.UnrealizedPnl.Should().Be(1_000m);
        state.DrawdownRatio.Should().Be((105_000m - 101_000m) / 105_000m);
    }

    [Fact]
    public void 現在値未指定なら含み損益とDDは0のまま_回帰()
    {
        // IADR-0036: 既定（引数省略）は production 現挙動を保持（含み 0・DD 0）。
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(9)) },
            Now, InitialCapital);

        state.UnrealizedPnl.Should().Be(0m);
        state.DrawdownRatio.Should().Be(0m);
    }

    // --- FR-10, FR-17, #257, IADR-0107: 金額集計は基準通貨（円）、建玉はローカル通貨 ---

    private static LedgerFill UsdFill(
        TradeSide side, PositionEffect effect, int qty, decimal price, DateTimeOffset at, decimal rate) =>
        new("AAPL", Market.UnitedStates, side, effect, qty, price, at, StopLossPrice: null, FxRateToBase: rate);

    [Fact]
    public void 外貨建ての取得額と当日発注累計は基準通貨で積まれる()
    {
        // 20 USD × 10 株 × 150 円 = 30,000 円。換算しなければ 200（円と誤読）で統制が桁で緩む。
        var state = PortfolioProjection.Project(
            new[] { UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(), rate: 150m) },
            Now, InitialCapital);

        state.InvestedCapital.Should().Be(30_000m);
        state.DailyOrderedAmount.Should().Be(30_000m);
    }

    [Fact]
    public void 外貨建ての実現損益は基準通貨で計上される()
    {
        // 20 USD で 10 株建て（レート 150）→ 25 USD で全決済（レート 150）。実現＝5 USD × 10 × 150 = 7,500 円。
        var state = PortfolioProjection.Project(
            new[]
            {
                UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(9), rate: 150m),
                UsdFill(TradeSide.Sell, PositionEffect.Close, 10, 25m, TodayAt(11), rate: 150m),
            },
            Now, InitialCapital);

        state.DailyRealizedPnl.Should().Be(7_500m);
        state.OpenPositionCount.Should().Be(0);
    }

    [Fact]
    public void 外貨建ての含み損益は建玉の約定時レートで換算される()
    {
        // 20 USD → 現在値 25 USD。含み＝5 USD × 10 株 × 150 = 7,500 円。
        var prices = new Dictionary<(string, Market), decimal> { [("AAPL", Market.UnitedStates)] = 25m };

        var state = PortfolioProjection.Project(
            new[] { UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(), rate: 150m) },
            Now, InitialCapital, prices);

        state.UnrealizedPnl.Should().Be(7_500m);
    }

    [Fact]
    public void 建玉の平均取得単価はローカル通貨のまま射影される()
    {
        // 損切り検知（市場監視）は現在値（ローカル通貨）と比較するため、建玉側は換算しない。
        var positions = PortfolioProjection.ProjectOpenPositions(
            new[] { UsdFill(TradeSide.Buy, PositionEffect.Open, 10, 20m, TodayAt(), rate: 150m) });

        positions.Should().ContainSingle();
        positions[0].AverageEntryPrice.Should().Be(20m);
    }

    // --- #337（#249 吸収）, IADR-0246: 取引日境界の市場別解釈 ---

    [Fact]
    public void 米国市場の約定は米国東部時間の取引日で当日判定される()
    {
        // 約定 = UTC 7/9 19:30（ET 7/9 15:30・JST では既に 7/10 4:30）。
        // 判定時点 = UTC 7/9 14:30（ET 7/9 10:30・JST 7/9 23:30）。
        // ET では同一取引日（7/9）＝当日。旧 JST 固定境界では約定が「翌日」に落ち、
        // 当日発注累計・同日再エントリー判定から漏れていた。
        var fillAt = new DateTimeOffset(2026, 7, 9, 19, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 9, 14, 30, 0, TimeSpan.Zero);

        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, fillAt) },
            now, InitialCapital);

        state.DailyOrderedAmount.Should().Be(10_000m);
        state.SymbolsTradedToday.Should().Contain(("AAPL", Market.UnitedStates));
    }

    [Fact]
    public void 米国市場の前取引日の実現損益は当日ではなく資金基準へ畳まれる()
    {
        // 建て（ET 7/8）→ 決済（ET 7/8 の引け近く）で +2,000。判定時点は ET 7/9 の場中。
        // 実現は「当日より前」＝ Capital（当日開始基準）へ入り、DailyRealizedPnl は 0。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, new DateTimeOffset(2026, 7, 8, 14, 0, 0, TimeSpan.Zero)),
                Fill(TradeSide.Sell, PositionEffect.Close, 10, 1_200m, new DateTimeOffset(2026, 7, 8, 19, 0, 0, TimeSpan.Zero)),
            },
            new DateTimeOffset(2026, 7, 9, 14, 30, 0, TimeSpan.Zero), InitialCapital);

        state.DailyRealizedPnl.Should().Be(0m);
        state.Capital.Should().Be(InitialCapital + 2_000m);
    }

    [Fact]
    public void 日本市場の約定はJSTの取引日で当日判定される()
    {
        // 約定 = JST 7/10 9:00（UTC 7/10 0:00）。判定時点 = JST 7/10 10:00。同一 JST 取引日＝当日。
        var state = PortfolioProjection.Project(
            new[]
            {
                Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m,
                    new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.FromHours(9)), symbol: "7203", market: Market.Japan),
            },
            new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.FromHours(9)), InitialCapital);

        state.DailyOrderedAmount.Should().Be(10_000m);
        state.SymbolsTradedToday.Should().Contain(("7203", Market.Japan));
    }

    [Fact]
    public void レート未記録の約定は従来どおり基準通貨建てとして積まれる_回帰()
    {
        // 既定 1＝日本株および列追加前の既存データ。現行挙動と等価であることを固定する。
        var state = PortfolioProjection.Project(
            new[] { Fill(TradeSide.Buy, PositionEffect.Open, 10, 1_000m, TodayAt(), symbol: "7203", market: Market.Japan) },
            Now, InitialCapital);

        state.InvestedCapital.Should().Be(10_000m);
        state.DailyOrderedAmount.Should().Be(10_000m);
    }
}
