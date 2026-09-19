using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #869, ADR-0041 決定2, IADR-0354:
// **統制上限の基準資金（equity）はブローカーの口座照会に由来し、照会できなければ新規建てを拒否する。**
//
// 計画の逐語（02_requirements FR-10 本文の括弧書き／06_technical/05_trading-assumptions §5 注記）:
//   「判定に用いる equity は**前営業日終値時点の USD 評価額**とする」
//   「equity の供給元はブローカーの**口座照会**とする……**照会できないときは新規建てを拒否する**（fail-closed）」
//   「**手仕舞い（Close）と損切りは止めない**」「**鮮度は日次でよい**」
//
// 従前の実装は「初期資金 ＋ 当日より前の実現損益」を基準にしており、**含み損益を含まない**点で計画と食い違っていた。
// 本書はその是正を固定する（T-10-508〜T-10-513。T-10-513 は PortfolioProjectionTests に置く）。
public class CapitalBaselineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 18, 0, 0, TimeSpan.Zero); // 木曜 14:00 ET
    private static readonly DateOnly Today = new(2026, 7, 9);

    private static OrderIntent Entry(decimal price, int quantity = 1) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.InternalPaper, quantity, price, PositionEffect.Open);

    private static OrderIntent Close(decimal price = 1_000m, int quantity = 10) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.InternalPaper, quantity, price, PositionEffect.Close);

    private static RiskManagementSettings Settings() => TradingDefaults.CreateSettings();

    private static PortfolioSnapshot Snapshot(decimal? capital) => new()
    {
        Capital = capital,
        OpenPositionCount = 0,
        InvestedCapital = 0m,
        DailyOrderedAmount = 0m,
        DailyRealizedPnl = 0m,
        UnrealizedPnl = 0m,
    };

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(dbName).Options);

    // T-10-508: 口座照会に由来する基準資金から 1 注文金額上限（equity の 25%）が解決される。
    // **境界の両側**を採る——内側は通り、1 円でも外は拒否される。値は口座照会の値だけで決まり、
    // 台帳の実現損益には依存しない。
    [Theory]
    [InlineData(2_000, 500, false)]   // equity 2,000 の 25% ＝ 500。ちょうどは通る
    [InlineData(2_000, 501, true)]    // 1 単位超で拒否
    [InlineData(8_000, 2_000, false)] // 口座照会の値が変われば上限も比例して動く
    [InlineData(8_000, 2_001, true)]
    public void 基準資金は口座照会由来の値で解決される(decimal equity, decimal notional, bool rejected)
    {
        var result = RiskEvaluator.Evaluate(Entry(notional), Settings(), Snapshot(equity));

        result.Reasons.Contains(RejectionReason.PerOrderAmountExceeded).Should().Be(rejected);
    }

    // T-10-509: **照会できないとき新規建ては拒否される**（fail-closed）。
    // ADR-0016 決定3・ADR-0028 と同じ形であり、新しい規律ではない。
    [Fact]
    public void 口座を照会できないとき新規建ては拒否される()
    {
        var result = RiskEvaluator.Evaluate(Entry(100m), Settings(), Snapshot(capital: null));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.CapitalBaselineUnavailable);
    }

    // T-10-509（続き・否定形）: 分母が分からない状態で「上限を満たした」という判定を作らない。
    // 🔴 比率上限の理由（1 注文 25% / 1 日 150% / 段階の総資金比 / 日次損失 2%）は**立たない**——
    // 立ててしまうと監査ログが「枠を使い切った」という起きていない事実を主張する。
    [Fact]
    public void 照会できないときは比率上限の拒否理由を捏造しない()
    {
        var snapshot = Snapshot(capital: null) with { DailyRealizedPnl = -1_000_000m };

        var result = RiskEvaluator.Evaluate(Entry(1_000_000m), Settings(), snapshot);

        result.Reasons.Should().Contain(RejectionReason.CapitalBaselineUnavailable);
        result.Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
        result.Reasons.Should().NotContain(RejectionReason.DailyOrderAmountExceeded);
        result.Reasons.Should().NotContain(RejectionReason.DailyLossLimitReached);
        result.Reasons.Should().NotContain(RejectionReason.StageCapitalCapExceeded);
    }

    // T-10-510: **同じ状況で手仕舞い・損切りは通る**（FR-10 の不変条件・ADR-0009）。
    // 照会できないことを理由に建玉を閉じられなくするのは統制ではなく事故である。
    [Fact]
    public void 照会できなくても手仕舞いと損切りは通る()
    {
        var result = RiskEvaluator.Evaluate(Close(), Settings(), Snapshot(capital: null));

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().BeEmpty();
    }

    // T-10-509（続き・空売り）: 空売り統制でも**分母の無い上限は判定しない**。
    // 🔴 0 を代入すると 1 銘柄あたり上限（equity の 10%）があらゆる注文で成立し、
    // 監査ログに `ShortExposureExceeded` という**起きていない事実**が残る（IADR-0354 決定6）。
    // equity に依存しない規則（借株可否・逆指値必須・株価下限）は**未供給でもそのまま効く**。
    [Fact]
    public void 照会できないとき空売りの1銘柄あたり上限は判定しない()
    {
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell,
            BrokerProvider.InternalPaper, 10, 100m, PositionEffect.Open, StopLossPrice: 110m);
        var context = new ShortSellOrderContext
        {
            Today = Today,
            BorrowRateAnnual = 0.10m,
            ShortPermit = true,
            SymbolShortExposure = 0m,
            TotalShortExposure = 0m,
            TotalExposure = 1_000_000m, // 空売り比率 50%（equity 非依存）は先に効かせない
        };

        var unsupplied = ShortSellEvaluator.Evaluate(
            intent, shortSellEnabled: true, TradingDefaults.CreateShortSellSettings().Limits, equity: null, context);
        var zero = ShortSellEvaluator.Evaluate(
            intent, shortSellEnabled: true, TradingDefaults.CreateShortSellSettings().Limits, equity: 0m, context);

        unsupplied.Should().NotContain(RejectionReason.ShortExposureExceeded,
            "分母が無いのに「エクスポージャ上限を超えた」と記録してはならない");
        // 対の肯定形: 0 は「資金が 0」という**判定できる値**であり、こちらでは立つ。
        zero.Should().Contain(RejectionReason.ShortExposureExceeded);
    }

    // T-10-511: 鮮度（実装判断・IADR-0354 決定4）。既定 4 日。**境界の両側**を採る。
    [Theory]
    [InlineData(4, true)]  // ちょうど 4 日前の観測は使える
    [InlineData(5, false)] // 超えたら「照会できていない」と同じ扱い
    public void 鮮度が切れた観測は基準資金として採らない(int daysAgo, bool supplied)
    {
        var dbName = Guid.NewGuid().ToString();
        var observedAt = Now.AddDays(-daysAgo);
        using var db = NewContext(dbName);
        var store = new EfCapitalBaselineStore(
            db, new FakeClock(Now, Today), new CapitalBaselineOptions());

        store.Record(3_000m, observedAt);

        (store.GetCurrent() is not null).Should().Be(supplied);
    }

    // T-10-515, FR-10, #869, IADR-0354 決定7: **0 以下の行は「判定できる値」として扱わない。**
    // 🔴 0 を分母にすると比率上限がすべて 0 になり、**損益ゼロ・枠未使用の平常状態でも**
    // `DailyLossLimitReached`（`0 <= -(0 × 2%)`）が立つ。しかも発注審査はその理由で
    // **翌営業日まで続く日次損失ロックアウトを実際に張る** —— 嘘の理由から本物の状態が生まれる。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 評価額が0以下の行は未供給として扱う(int equity)
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfCapitalBaselineStore(
            db, new FakeClock(Now, Today), new CapitalBaselineOptions());

        store.Record(equity, Now.AddDays(-1));

        store.GetCurrent().Should().BeNull("分母が定義できない値を「判定できる値」として通さない");
    }

    // T-10-515（否定形・本体）: 🔴 **平常状態で `DailyLossLimitReached` を立てない。**
    // 供給が 0 以下なら基準資金は `null` になり、拒否理由は `CapitalBaselineUnavailable` だけになる。
    [Fact]
    public void 評価額が0のとき日次損失上限に達したと記録しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfCapitalBaselineStore(
            db, new FakeClock(Now, Today), new CapitalBaselineOptions());
        store.Record(0m, Now.AddDays(-1));

        // 損益ゼロ・枠未使用の平常状態で審査する。
        var snapshot = Snapshot(store.GetCurrent()?.EquityInBase);
        var result = RiskEvaluator.Evaluate(Entry(100m), Settings(), snapshot);

        result.Reasons.Should().Contain(RejectionReason.CapitalBaselineUnavailable);
        result.Reasons.Should().NotContain(RejectionReason.DailyLossLimitReached,
            "当日の損失は 0 である。起きていない到達を記録すると、発注審査が本物のロックアウトを張る");
        result.Reasons.Should().NotContain(RejectionReason.StageCapitalCapExceeded);
        result.Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
        result.Reasons.Should().NotContain(RejectionReason.DailyOrderAmountExceeded);
    }

    // T-10-515（対の肯定形）: 🔴 **0 を素通しすると 4 件の嘘が立つ**ことを固定する。
    // 本テストが緑であること自体が、上の門が無ければ何が起きるかの証跡である。
    [Fact]
    public void 参照_基準資金に0が入ると平常状態でも4件の拒否理由が立つ()
    {
        var result = RiskEvaluator.Evaluate(Entry(100m), Settings(), Snapshot(0m));

        result.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
        result.Reasons.Should().Contain(RejectionReason.StageCapitalCapExceeded);
        result.Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
        result.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
    }

    // T-10-512: **当日の観測は基準資金を動かさない。** 計画 §5 注記は「日中の評価損益で上限を動かすと、
    // 含み益で上限が緩み含み損で締まるという逆方向の作用が起きる」として明示的に禁じている。
    [Fact]
    public void 当日中に届いた観測は基準資金を動かさない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfCapitalBaselineStore(
            db, new FakeClock(Now, Today), new CapitalBaselineOptions());

        store.Record(3_000m, Now.AddDays(-1));   // 前取引日の終わり
        store.Record(9_999m, Now);               // 当日のセッション中（含み益で膨らんだ値）

        var baseline = store.GetCurrent();

        baseline.Should().NotBeNull();
        baseline!.EquityInBase.Should().Be(3_000m, "判定に用いるのは前営業日終値時点の評価額である");
    }

    // 同一取引日では**最後の観測**を保ち、逆行する観測は無視する（終値に最も近い観測を残すため）。
    [Fact]
    public void 同一取引日では最後の観測を保ち逆行する観測は無視する()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfCapitalBaselineStore(
            db, new FakeClock(Now, Today), new CapitalBaselineOptions());
        var yesterday = Now.AddDays(-1);

        store.Record(3_000m, yesterday.AddHours(-6));
        store.Record(3_500m, yesterday);            // より新しい＝採る
        store.Record(1m, yesterday.AddHours(-12));  // 逆行＝無視する

        store.GetCurrent()!.EquityInBase.Should().Be(3_500m);
    }

    // 供給経路: 口座観測（BrokerAccountObserved）から基準資金が畳まれる。
    // 🔴 評価額が取れなかった観測は**書かない**——直前の値を書き直すと「今日も照会できた」という
    // 起きていない事実を記録することになり、鮮度の検査が効かなくなる。
    [Theory]
    [InlineData(3_000, true)]
    [InlineData(null, false)]
    public void 口座観測から基準資金が記録される_評価額が無い観測は書かない(int? equity, bool recorded)
    {
        var baseline = FakeCapitalBaseline.NotObserved();
        var handler = new BrokerAccountObservedHandler(
            FakeBrokerAccountObservations.NotObserved(),
            baseline,
            NullLogger<BrokerAccountObservedHandler>.Instance);

        handler.Handle(new BrokerAccountObserved(
            BrokerProvider.MoomooSimulate,
            new BrokerAccountState(AccountType.Margin, EquityInBase: equity),
            Now));

        (baseline.LastRecorded is not null).Should().Be(recorded);
    }

    // 合成: スナップショットの基準資金は**口座照会のストアだけ**から来る（台帳射影は供給しない）。
    [Fact]
    public void スナップショットの基準資金は口座照会のストアから来る()
    {
        var state = new PortfolioState { LedgerEquity = 777_777m };

        var supplied = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(state), new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(),
            capitalBaseline: FakeCapitalBaseline.Of(3_000m)).Build();
        var unsupplied = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(state), new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(),
            capitalBaseline: FakeCapitalBaseline.NotObserved()).Build();

        supplied.Capital.Should().Be(3_000m);
        // 🔴 台帳のエクイティ（777,777）が基準資金へ漏れていないこと。
        unsupplied.Capital.Should().BeNull();
    }
}
