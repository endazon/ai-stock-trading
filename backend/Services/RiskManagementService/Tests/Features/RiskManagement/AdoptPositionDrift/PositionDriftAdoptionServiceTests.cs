using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.AdoptPositionDrift;
using RiskManagementService.Features.RiskManagement.GetFills;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350: 台帳とブローカーの乖離を、利用者の承認つきで台帳へ取り込む。
//
// 稼働環境の実測（#849）: 利用者が証券会社のアプリから全株を売却し、ブローカーの建玉は 0・台帳は 3,381 株のまま。
// 実在しない建玉が段階資金の枠を占有し、`stageCapitalRemaining = 332.47` で新規建てが 1 本も出なくなった。
// 本テストは「取り込めること」だけでなく、**取り込んだ結果として枠が回復すること**と、
// **取り込んではならない状況で台帳が 1 行も動かないこと**を固定する。
public class PositionDriftAdoptionServiceTests
{
    private const string Symbol = "AAPL";
    private const Market Us = Market.UnitedStates;

    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 40, 0, TimeSpan.Zero);

    // 建玉を建てた時刻（前日）と、乖離を観測した時刻（取り込みの 5 分前）。
    private static readonly DateTimeOffset OpenedAt = Now.AddDays(-1);
    private static readonly DateTimeOffset ObservedAt = Now.AddMinutes(-5);

    private sealed class PriceSource(decimal? price) : ICurrentPriceSource
    {
        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(
            IReadOnlyList<OpenPosition> positions) =>
            price is { } p
                ? positions.ToDictionary(x => (x.Symbol, x.Market), _ => p)
                : new Dictionary<(string Symbol, Market Market), decimal>();
    }

    // 1 テスト分の世界（台帳・観測・乖離の追跡状態・時計）。
    private sealed class World
    {
        public InMemoryPortfolioLedgerStore Ledger { get; } = new();

        public InMemoryBrokerPositionObservationStore Observations { get; } = new();

        public InMemoryPositionDriftStateStore DriftState { get; } = new();

        public FakeClock Clock { get; } = new(Now, DateOnly.FromDateTime(Now.UtcDateTime));

        public PositionDriftTracker Tracker => new(DriftState, NullLogger<PositionDriftTracker>.Instance);

        public PositionDriftAdoptionService Service(decimal? currentPrice = null) =>
            new(Ledger, Observations, Tracker, new PriceSource(currentPrice), Clock);

        public Guid Open(TradeSide side, int quantity, decimal price, DateTimeOffset? at = null, string symbol = Symbol)
        {
            var decisionId = Guid.NewGuid();
            var when = at ?? OpenedAt;
            Ledger.AppendApproval(
                decisionId,
                new OrderIntent(symbol, Us, side, ProductType.Cash, BrokerProvider.MoomooSimulate,
                    quantity, price, PositionEffect.Open, StopLossPrice: price * 0.95m),
                when);
            Ledger.AppendFill(decisionId, $"open-{decisionId:N}", quantity, price, when);
            return decisionId;
        }

        /// <summary>ブローカ建玉を観測し、乖離の検知（本番のハンドラと同じ手順）を <paramref name="times"/> 回まわす。</summary>
        public void Observe(int times, DateTimeOffset observedAt, params BrokerPositionSnapshot[] positions)
        {
            for (var i = 0; i < times; i++)
            {
                Observations.Record(positions, observedAt.AddSeconds(i));
                var drifts = PositionDriftDetector.Detect(
                    PortfolioProjection.ProjectOpenPositions(Ledger.GetFills()), positions);
                Tracker.ShouldReport(drifts);
            }
        }

        public int LedgerQuantity(string symbol = Symbol) =>
            PortfolioProjection.ProjectOpenPositions(Ledger.GetFills())
                .Where(p => p.Symbol == symbol)
                .Sum(p => p.Side == TradeSide.Buy ? p.Quantity : -p.Quantity);

        public SizingContextService Sizing()
        {
            var provider = new LedgerPortfolioStateProvider(
                Ledger,
                new InMemoryWorkingEntryOrderSource(Ledger, new InMemoryOrderActivityStore()),
                Clock,
                initialCapital: 1_133_333m);
            var snapshots = new PortfolioSnapshotBuilder(
                provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
                FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(), capitalBaseline: FakeCapitalBaseline.Of(1_133_333m));
            return new SizingContextService(snapshots, new InMemoryRiskSettingsStore());
        }
    }

    private static PositionDriftAdoptionCommand Command(string reason = "moomoo アプリから全株を手動売却した", string symbol = Symbol) =>
        new(symbol, Us, reason);

    // ---------------------------------------------------------------------------------------------
    // 正常系
    // ---------------------------------------------------------------------------------------------

    // T-10-443: 報告済みの乖離（台帳 3,381・ブローカー 0）を取り込むと、台帳は観測値へ合い、
    // 監査イベントが「誰が・なぜ・取り込み前後の数量・観測・実現損益は未記録」を運ぶ。
    [Fact]
    public void 報告済みの乖離を取り込むと台帳が観測値へ合い監査イベントが事実を運ぶ()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 3_381, 335m);
        w.Observe(times: 2, ObservedAt);

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Accepted.Should().BeTrue();
        w.LedgerQuantity().Should().Be(0, "観測されたブローカーの建玉（0 株）へ合わせる");

        var e = outcome.Adopted!;
        e.Symbol.Should().Be(Symbol);
        e.Market.Should().Be(Us);
        e.LedgerQuantityBefore.Should().Be(3_381);
        e.LedgerQuantityAfter.Should().Be(0);
        e.BrokerQuantity.Should().Be(0);
        e.ObservedAt.Should().Be(ObservedAt.AddSeconds(1), "取り込みの根拠は最新の観測である");
        e.CostBasisPrice.Should().Be(335m);
        e.Actor.Should().Be("endazon");
        e.Reason.Should().Be("moomoo アプリから全株を手動売却した");
        e.AdoptedAt.Should().Be(Now);
        e.RealizedPnlRecorded.Should().BeFalse("システム外の売買は約定価格が分からない（IADR-0350 決定 3）");
        e.ReferencePrice.Should().BeNull("現在値が取れなければ推定もしない（0 で埋めない）");
        e.EstimatedPnlInBase.Should().BeNull();
    }

    // T-10-444: **結果の assert。** 実在しない建玉が占有していた段階資金の残枠が、取り込みで実際に回復する
    // （#849 の実害＝ stageCapitalRemaining が尽きて新規建てが 1 本も出ない）。
    // 併せて、取り込みが動かしてはならない値（当日発注の残枠・基準資金・連敗・DD）が動かないことを固定する。
    [Fact]
    public void 取り込み後にサイジング文脈の段階資金の残枠が回復する()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 3_381, 335.1225m); // 取得額 1,132,999.17… ＝ 稼働環境の実測とほぼ同じ占有
        w.Observe(times: 2, ObservedAt);

        var before = w.Sizing().Build();
        before.StageCapitalRemaining.Should().BeLessThan(336m, "取り込み前は 1 株も買えない（実測 332.47 と同じ状態）");

        w.Service().Adopt(Command(), "endazon").Accepted.Should().BeTrue();

        var after = w.Sizing().Build();
        after.StageCapitalRemaining.Should().Be(1_133_333m, "実在しない建玉が消え、段階資金を満額使える");
        after.DailyOrderRemaining.Should().Be(before.DailyOrderRemaining, "取り込みは新規建ての発注ではない");
        after.Capital.Should().Be(before.Capital, "実現損益を記録しないため基準資金は動かない");
        after.ConsecutiveLosses.Should().Be(before.ConsecutiveLosses);
        after.DrawdownRatio.Should().Be(before.DrawdownRatio);
    }

    // T-10-445: 部分的な乖離（台帳 100・ブローカー 40）は差分だけ減り、残りの建玉の取得単価と損切り価格は変わらない。
    [Fact]
    public void 部分的な乖離は差分だけ減り残りの建玉の取得単価と損切りは変わらない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Observe(times: 2, ObservedAt, new BrokerPositionSnapshot(Symbol, Us, 40, 200m));

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Accepted.Should().BeTrue();
        outcome.Adopted!.LedgerQuantityAfter.Should().Be(40);
        var position = PortfolioProjection.ProjectOpenPositions(w.Ledger.GetFills()).Single();
        position.Quantity.Should().Be(40);
        position.AverageEntryPrice.Should().Be(200m);
        position.StopLossPrice.Should().Be(190m, "一部が消えても、残りの建玉の損切りラインは保つ（IADR-0035 と同じ規則）");
    }

    // T-10-446: 空売り建玉（台帳 −50・ブローカー −20）も同じ規則で減る（減少の方向は買い）。
    [Fact]
    public void 空売り建玉の乖離も減らす方向なら取り込める()
    {
        var w = new World();
        w.Open(TradeSide.Sell, 50, 100m);
        w.Observe(times: 2, ObservedAt, new BrokerPositionSnapshot(Symbol, Us, -20, 100m));

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Accepted.Should().BeTrue();
        w.LedgerQuantity().Should().Be(-20);
        outcome.Adopted!.LedgerQuantityBefore.Should().Be(-50);
    }

    // T-10-447: 現在値が取れるときだけ**推定**を監査イベントへ載せる。推定は台帳のどの数値にも入らない。
    [Fact]
    public void 推定損益は監査イベントにだけ載り台帳の実現損益には入らない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 3_381, 335m);
        w.Observe(times: 2, ObservedAt);

        var outcome = w.Service(currentPrice: 332.83m).Adopt(Command(), "endazon");

        outcome.Adopted!.ReferencePrice.Should().Be(332.83m);
        outcome.Adopted.EstimatedPnlInBase.Should().Be((332.83m - 335m) * 3_381);
        outcome.Adopted.RealizedPnlRecorded.Should().BeFalse();

        var state = PortfolioProjection.Project(w.Ledger.GetFills(), Now, 1_133_333m);
        state.DailyRealizedPnl.Should().Be(0m, "推定値を確定値のように記録しない");
        state.LedgerEquity.Should().Be(1_133_333m);
    }

    // ---------------------------------------------------------------------------------------------
    // 否定形: いずれも台帳が 1 行も動かないこと
    // ---------------------------------------------------------------------------------------------

    // T-10-448: 観測が一度も届いていない（照会不能を含む＝不明）なら拒否する。
    [Fact]
    public void 観測が無ければ拒否し台帳は動かない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.ObservationUnavailable);
        outcome.Adopted.Should().BeNull();
        w.LedgerQuantity().Should().Be(100);
    }

    // T-10-449: 観測が古い（60 分超）なら拒否する。境界（ちょうど 60 分）は受理する。
    [Theory]
    [InlineData(61, false)]
    [InlineData(60, true)]
    public void 観測が古ければ拒否する(int ageMinutes, bool accepted)
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        // 2 回目の観測（＝最新）がちょうど ageMinutes 前になるよう置く。
        w.Observe(times: 2, Now.AddMinutes(-ageMinutes).AddSeconds(-1));

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Accepted.Should().Be(accepted);
        if (!accepted)
        {
            outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.ObservationStale);
            w.LedgerQuantity().Should().Be(100);
        }
    }

    // T-10-450: 理由が空なら拒否する（空白だけも不可）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 理由が無ければ拒否し台帳は動かない(string reason)
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Observe(times: 2, ObservedAt);

        var outcome = w.Service().Adopt(Command(reason), "endazon");

        outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.ReasonRequired);
        w.LedgerQuantity().Should().Be(100);
    }

    // T-10-451: **冪等。** 二重に取り込んでも台帳は二重に減らない（ロングがショートへ反転しない）。
    [Fact]
    public void 二重に取り込んでも台帳は二重に減らない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 3_381, 335m);
        w.Observe(times: 2, ObservedAt);
        w.Service().Adopt(Command(), "endazon").Accepted.Should().BeTrue();

        var second = w.Service().Adopt(Command(), "endazon");

        second.Rejection.Should().Be(PositionDriftAdoptionRejection.NoDrift);
        second.Adopted.Should().BeNull("2 回目は監査イベントも出さない");
        w.LedgerQuantity().Should().Be(0, "−3,381 へ反転していない");
        w.Ledger.GetFills().Count(f => f.IsDriftAdoption).Should().Be(1);
    }

    // T-10-452: 並行の二重投入（読み取りから追記までの間に他方が先に書いた）は、冪等キーで 1 件に絞られる。
    [Fact]
    public void 同じ観測に対する同じ取り込みは冪等キーで一件に絞られる()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        LedgerDriftAdoption Row() => new(
            Guid.NewGuid(), Symbol, Us, TradeSide.Sell, 3_381, 335m, 1m, 3_381, 0, ObservedAt, "endazon", "理由", Now);

        ledger.AppendDriftAdoption(Row()).Should().BeTrue();
        ledger.AppendDriftAdoption(Row()).Should().BeFalse("Id が違っても、同じ観測・同じ取り込み前数量なら同じ取り込みである");
    }

    // T-10-453: まだ報告されていない乖離（連続観測 1 回＝一過性の未反映かもしれない）は拒否する。
    [Fact]
    public void まだ報告されていない乖離は拒否し台帳は動かない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Observe(times: 1, ObservedAt);

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.DriftNotReported);
        w.LedgerQuantity().Should().Be(100);
    }

    // T-10-454: 乖離の無い銘柄は拒否する（利用者が任意の銘柄の台帳を書き換える API ではない）。
    [Fact]
    public void 乖離の無い銘柄は拒否する()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Observe(times: 2, ObservedAt, new BrokerPositionSnapshot(Symbol, Us, 100, 200m));

        w.Service().Adopt(Command(), "endazon").Rejection.Should().Be(PositionDriftAdoptionRejection.NoDrift);
        w.LedgerQuantity().Should().Be(100);
    }

    // T-10-455: 台帳に無い建玉・数量の増加・方向の反転は取り込まない
    // （取得単価も損切りも分からない建玉を、システムの管理下にあるように見せない）。
    [Theory]
    [InlineData(0, 50)]      // BrokerOnly: 台帳に無い建玉
    [InlineData(100, 150)]   // 数量の増加
    [InlineData(100, -30)]   // 方向の反転
    public void 減らす方向でない乖離は拒否し台帳は動かない(int ledgerQuantity, int brokerQuantity)
    {
        var w = new World();
        if (ledgerQuantity != 0)
            w.Open(TradeSide.Buy, ledgerQuantity, 200m);
        w.Observe(times: 2, ObservedAt, new BrokerPositionSnapshot(Symbol, Us, brokerQuantity, 200m));

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.UnsupportedDirection);
        w.LedgerQuantity().Should().Be(ledgerQuantity);
    }

    // T-10-456: 処理中の決済（承認済み・未約定）があれば拒否する（約定が後から届くと二重に減る）。
    [Fact]
    public void 処理中の決済があれば拒否し台帳は動かない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Ledger.AppendApproval(
            Guid.NewGuid(),
            new OrderIntent(Symbol, Us, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                100, 199m, PositionEffect.Close),
            Now.AddMinutes(-10));
        w.Observe(times: 2, ObservedAt);

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.CloseInFlight);
        w.LedgerQuantity().Should().Be(100);
    }

    // T-10-457: 観測より後に台帳の当該銘柄が動いていれば拒否する（観測が台帳に対して古い）。
    [Fact]
    public void 観測より後に台帳が動いていれば拒否する()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Observe(times: 2, ObservedAt, new BrokerPositionSnapshot(Symbol, Us, 40, 200m));
        // 観測の後に、同じ銘柄の決済が約定して台帳が 100 → 70 へ動いた（乖離の内容は報告済みのものと変わる）。
        var closeId = Guid.NewGuid();
        w.Ledger.AppendApproval(
            closeId,
            new OrderIntent(Symbol, Us, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                30, 199m, PositionEffect.Close),
            Now.AddMinutes(-2));
        w.Ledger.AppendFill(closeId, "close-1", 30, 199m, Now.AddMinutes(-1));

        var outcome = w.Service().Adopt(Command(), "endazon");

        outcome.Rejection.Should().Be(PositionDriftAdoptionRejection.LedgerMovedAfterObservation);
        w.LedgerQuantity().Should().Be(70);
    }

    // T-10-458: 複数銘柄の乖離は 1 件ずつ取り込める（1 件取り込んでも、残りの銘柄は報告済みのまま）。
    [Fact]
    public void 複数銘柄の乖離を一件ずつ取り込める()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Open(TradeSide.Buy, 10, 400m, symbol: "MSFT");
        w.Observe(times: 2, ObservedAt);

        w.Service().Adopt(Command(), "endazon").Accepted.Should().BeTrue();
        w.Service().Adopt(Command(symbol: "MSFT"), "endazon").Accepted.Should().BeTrue();

        w.LedgerQuantity().Should().Be(0);
        w.LedgerQuantity("MSFT").Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // 射影: 取り込み行は数量だけを動かす
    // ---------------------------------------------------------------------------------------------

    // T-10-440: 取り込み行は実現損益・当日発注累計・連敗・同日売買銘柄を動かさず、取得額と保有建玉数だけを減らす。
    [Fact]
    public void 取り込み行は在庫だけを減らし実現損益も当日発注も連敗も動かさない()
    {
        var w = new World();
        // 為替換算の端数が出る単価とレート（保存した単価で畳むと ±1e-27 の「損益」が生まれ得る形）。
        var decisionId = Guid.NewGuid();
        w.Ledger.AppendApproval(
            decisionId,
            new OrderIntent("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                300, 2_871m, PositionEffect.Open, StopLossPrice: 2_700m, FxRateToBase: 0.0067391m),
            Now.AddMinutes(-30));
        w.Ledger.AppendFill(decisionId, "jp-1", 100, 2_871m, Now.AddMinutes(-30));
        w.Ledger.AppendFill(decisionId, "jp-2", 200, 2_873.3333m, Now.AddMinutes(-20));
        var baseline = PortfolioProjection.Project(w.Ledger.GetFills(), Now, 3_000m);
        baseline.DailyOrderedAmount.Should().BeGreaterThan(0m, "前提: 当日の新規建てがある（比較を空虚にしない）");

        var position = PortfolioProjection.ProjectOpenPositions(w.Ledger.GetFills()).Single();
        w.Ledger.AppendDriftAdoption(new LedgerDriftAdoption(
            Guid.NewGuid(), "7203", Market.Japan, TradeSide.Sell, 300, position.AverageEntryPrice,
            position.FxRateToBase, 300, 0, ObservedAt, "endazon", "理由", Now)).Should().BeTrue();

        var state = PortfolioProjection.Project(w.Ledger.GetFills(), Now, 3_000m);

        state.OpenPositionCount.Should().Be(0);
        state.InvestedCapital.Should().Be(0m);
        state.DailyRealizedPnl.Should().Be(0m, "丸め誤差の『損益』も生まない（平均取得単価そのもので畳む）");
        state.LedgerEquity.Should().Be(baseline.LedgerEquity);
        state.ConsecutiveLosses.Should().Be(baseline.ConsecutiveLosses);
        state.DailyOrderedAmount.Should().Be(baseline.DailyOrderedAmount, "取り込みは新規建ての発注ではない");
        state.SymbolsTradedToday.Should().BeEquivalentTo(baseline.SymbolsTradedToday);
    }

    // T-10-441: 取り込みで建玉が消えたら、建玉一覧（市場監視の損切り検知・手仕舞いの入力）からも消える。
    [Fact]
    public void 全量を取り込んだ建玉は建玉一覧から消える()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 3_381, 335m);
        w.Observe(times: 2, ObservedAt);

        w.Service().Adopt(Command(), "endazon");

        PortfolioProjection.ProjectOpenPositions(w.Ledger.GetFills()).Should().BeEmpty();
    }

    // T-10-442: 取り込み行はエクイティのピーク（DD の入力）を動かさない。
    [Fact]
    public void 取り込み行はエクイティのピークを動かさない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        var before = PortfolioValuation.EquityHighWaterMark(w.Ledger.GetFills(), 3_000m, 3_000m);
        w.Observe(times: 2, ObservedAt);
        w.Service().Adopt(Command(), "endazon");

        PortfolioValuation.EquityHighWaterMark(w.Ledger.GetFills(), 3_000m, 3_000m).Should().Be(before);
    }

    // T-10-459: **取り込み行は約定ではない。** 報告書が読む期間約定（GET /risk-controls/fills の実体）へは返さない
    // ——返すと「平均取得単価で売った損益 0 の決済」が確定値として集計される。
    [Fact]
    public void 取り込み行は期間約定に含めない()
    {
        var w = new World();
        w.Open(TradeSide.Buy, 100, 200m);
        w.Observe(times: 2, ObservedAt);
        w.Service().Adopt(Command(), "endazon");

        var day = DateOnly.FromDateTime(Now.UtcDateTime);
        var fills = PeriodFillQuery.InTradingDayRange(w.Ledger.GetFills(), day.AddDays(-7), day.AddDays(7));

        fills.Should().ContainSingle("建てた約定 1 件だけ").Which.IsDriftAdoption.Should().BeFalse();
    }

    // T-10-460: 強制買戻しの推定の根拠（自らの決済指示）に、取り込み行を並べない。
    [Fact]
    public void 取り込み行は自らの決済指示として根拠に並べない()
    {
        var w = new World();
        w.Open(TradeSide.Sell, 50, 100m);
        w.Observe(times: 2, ObservedAt, new BrokerPositionSnapshot(Symbol, Us, -20, 100m));
        w.Service().Adopt(Command(), "endazon");

        BuyInInference.CoveringFillsFor(w.Ledger.GetFills(), Symbol, Us).Should().BeEmpty();
    }

    // TradingDefaults の既定（段階 0 の発注可能額＝総資金の 100%）に依存していることを明示する。
    [Fact]
    public void 前提_段階0の発注可能額は総資金の全額である()
    {
        TradingDefaults.CreateStageSettings().OrderableCapFor(1_133_333m).Should().Be(1_133_333m);
    }
}
