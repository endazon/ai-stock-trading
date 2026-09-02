using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetShortSellingStatus;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-06, SC-03, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// SC-03「維持率・空売りの現況」の集約。
//
// **本テストの主眼は「値が正しいこと」ではなく「供給が無いことを供給が無いと言うこと」である。**
// 維持率は計画（05_screens SC-03）が「本画面の最上位に置く。マージンコールは口座を失う唯一の経路である」と
// 定めた指標であり、未供給を 0 や空列で運ぶと画面は正常な統制として描いてしまう
// （#403 の ControlViolationCount 既定 0 が「違反なし」に見えた fail-open と同型）。
public class ShortSellingStatusServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 6, 3, 0, 0, TimeSpan.Zero);

    private sealed class FakeLedger(params LedgerFill[] fills) : IPortfolioLedgerStore
    {
        public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt, decimal? fxRateBaseToDisplay = null) { }
        public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt, BrokerProvider? provider = null) => true;
        public IReadOnlyList<LedgerFill> GetFills() => fills;
        public PositionEffect? FindApprovedPositionEffect(Guid decisionId) => null;
        public OrderIntent? FindApprovedIntent(Guid decisionId) => null;
        public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter) => 0;
    }

    private sealed class FixedSnapshotSource(MaintenanceMarginSnapshot? snapshot) : IMaintenanceMarginSnapshotSource
    {
        public MaintenanceMarginSnapshot? GetCurrent() => snapshot;
    }

    private sealed class FakePriceSource(Dictionary<(string Symbol, Market Market), decimal> prices) : ICurrentPriceSource
    {
        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(
            IReadOnlyList<OpenPosition> positions) => prices;
    }

    private static LedgerFill Fill(TradeSide side, int qty, decimal price, string symbol) =>
        new(symbol, Market.UnitedStates, side, PositionEffect.Open, qty, price, At);

    private static MarginPosition Short(string symbol, decimal price, int quantity, decimal requiredMargin) =>
        new()
        {
            Symbol = symbol,
            Market = Market.UnitedStates,
            Side = TradeSide.Sell,
            ProductType = ProductType.ShortSell,
            Quantity = quantity,
            PriceUsd = price,
            RequiredMarginUsd = requiredMargin,
        };

    // FR-21, SC-03, #470: 当日を固定する時計。強制買戻しの発生回数の期間（当月）はここから決まる。
    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.Year, today.Month, today.Day, 3, 0, 0, TimeSpan.Zero);

        public DateOnly Today => today;
    }

    // 既定の当日。2026-08-06 は木曜（当月の営業日は 8/3〜8/6 の 4 日）。
    private static readonly DateOnly Today = new(2026, 8, 6);

    private static ShortSellingStatusService Create(
        MaintenanceMarginSnapshot? snapshot = null,
        LedgerFill[]? fills = null,
        Dictionary<(string Symbol, Market Market), decimal>? prices = null,
        IPositionObservationArrivalStore? observationArrivals = null,
        IBuyInInferenceStore? buyInInferences = null,
        DateOnly? today = null) =>
        new(new InMemoryRiskSettingsStore(),
            new FakeLedger(fills ?? []),
            new FixedSnapshotSource(snapshot),
            observationArrivals ?? new InMemoryPositionObservationArrivalStore(),
            buyInInferences ?? new InMemoryBuyInInferenceStore(),
            new FixedClock(today ?? Today),
            new WeekendBusinessCalendar(),
            prices is null ? null : new FakePriceSource(prices));

    // ---- 維持率（ADR-0016 決定7・画面最上位） ----

    // T-340-01: **供給元が無いことを「未供給」として宣言する。** 0 でも「建玉なし」でもない。
    // 既定アダプタ（UnavailableMaintenanceMarginSnapshotSource）が null を返す現状がこれに当たる。
    [Fact]
    public void 維持率の供給元が無ければ未供給として宣言する()
    {
        var view = Create(snapshot: null).Build();

        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.MaintenanceMarginRatio.Should().BeNull();
        view.AppliedMaintenanceMarginThreshold.Should().BeNull();
        view.AppliedMaintenanceRecoveryTarget.Should().BeNull();
    }

    // T-340-02: 既定アダプタ（本番の現行構成）を通しても同じ結論になる。
    // フェイクだけで確かめると「本番の既定が実は値を返している」場合に気づけない。
    [Fact]
    public void 既定アダプタ構成では維持率が未供給になる()
    {
        var service = new ShortSellingStatusService(
            new InMemoryRiskSettingsStore(),
            new FakeLedger(),
            new UnavailableMaintenanceMarginSnapshotSource(),
            new InMemoryPositionObservationArrivalStore(),
            new InMemoryBuyInInferenceStore(),
            new FixedClock(Today),
            new WeekendBusinessCalendar());

        service.Build().MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
    }

    // T-340-03: 供給があるときは値・適用閾値・回復目標（閾値 + 5pt）を返す。
    // 株価 $100 では自前の 40% が規制側（max(5/100, 30%) = 30%）より厳しいため 40%、回復目標は 45%。
    [Fact]
    public void 維持率の供給があれば値と適用閾値と回復目標を返す()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };

        var view = Create(snapshot).Build();

        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.Available);
        view.MaintenanceMarginRatio.Should().Be(0.40m);
        view.AppliedMaintenanceMarginThreshold.Should().Be(0.40m);
        view.AppliedMaintenanceRecoveryTarget.Should().Be(0.45m);
    }

    // T-340-04: **回復目標は閾値に連動する**（計画 §5）。規制側が効く低位株（$8）では
    // 閾値 max(5/8, 30%) = 62.5%、回復目標は 67.5% になる（40%/45% の固定値ではない）。
    [Fact]
    public void 回復目標は適用閾値に連動する()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 5_000m,
            Positions = [Short("PENNY", 8m, 1_000, 5_000m)],
        };

        var view = Create(snapshot).Build();

        view.AppliedMaintenanceMarginThreshold.Should().Be(0.625m);
        view.AppliedMaintenanceRecoveryTarget.Should().Be(0.675m);
    }

    // T-340-05: IADR-0133 決定8 と同じ向きに倒す。壊れた建玉が混ざるスナップショットは
    // **「良く見える維持率」を出さない**（壊れた建玉だけを除くと分母が縮んで実際より良く見える）。
    [Fact]
    public void 信頼できないスナップショットは未供給として扱う()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m), Short("BROKEN", 0m, 10, 1m)],
        };

        var view = Create(snapshot).Build();

        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.MaintenanceMarginRatio.Should().BeNull();
    }

    // T-340-06: 建玉が 1 件も無いのは**異常ではない**（維持率という概念が成立しないだけ）。
    // 未供給と同じ表示にすると「統制が働いていない」と読ませてしまう。
    [Fact]
    public void 信用建玉が無ければ維持率は概念として成立しない()
    {
        var snapshot = new MaintenanceMarginSnapshot { NetEquityUsd = 3_000m, Positions = [] };

        Create(snapshot).Build().MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotApplicable);
    }

    // T-340-07: 設定値（自前閾値 40%・回復目標オフセット 5pt・空売り比率上限 50%）は常に返す。
    // 画面が閾値を直書きしないため（Stage1GateCriteria と同じ方針）、供給が無くても設定値だけは要る。
    [Fact]
    public void 設定上の閾値と上限は供給が無くても返す()
    {
        var defaults = TradingDefaults.CreateShortSellSettings().Limits;
        var view = Create(snapshot: null).Build();

        view.ConfiguredMaintenanceMarginThreshold.Should().Be(defaults.MaintenanceMarginThreshold);
        view.MaintenanceRecoveryTargetOffset.Should().Be(defaults.MaintenanceRecoveryTargetOffset);
        view.ShortExposureRatioCap.Should().Be(defaults.ExposureRatioCap);
    }

    // ---- 空売り比率（ADR-0016 決定9） ----

    // T-340-08: 建玉が無ければ空売り比率は概念として成立しない（異常ではない）。
    [Fact]
    public void 建玉が無ければ空売り比率は概念として成立しない()
    {
        Create().Build().ShortExposureAvailability.Should().Be(MetricAvailability.NotApplicable);
    }

    // T-340-09: **分母は建玉総額＝時価である**（ADR-0016 決定9）。現在値が 1 件でも欠ければ算出しない。
    // 取得原価で代用すると「空売り比率」という名前の別物になり、上限 50% の判定を誤らせる。
    [Fact]
    public void 現在値が欠ける建玉があれば空売り比率を算出しない()
    {
        var view = Create(fills: [Fill(TradeSide.Sell, 10, 100m, "AAPL")]).Build();

        view.ShortExposureAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.ShortExposureRatio.Should().BeNull();
        view.Positions.Single().MarketValueAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.Positions.Single().MarketValueUsd.Should().BeNull();
    }

    // T-340-10: 現在値が揃えば時価で算出する（ショート $2,000 ÷ 総額 $5,000 ＝ 40%）。
    [Fact]
    public void 現在値が揃えば空売り比率を時価で算出する()
    {
        var view = Create(
            fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT"), Fill(TradeSide.Buy, 10, 280m, "LONG")],
            prices: new Dictionary<(string, Market), decimal>
            {
                [("SHRT", Market.UnitedStates)] = 200m,
                [("LONG", Market.UnitedStates)] = 300m,
            }).Build();

        view.ShortExposureAvailability.Should().Be(MetricAvailability.Available);
        view.ShortExposureRatio.Should().Be(0.40m);
    }

    // ---- 保有ポジション（ADR-0016 決定15） ----

    // T-340-11: **建玉の方向（ロング / ショート）**は供給がある。符号付き在庫の向きをそのまま返す。
    [Fact]
    public void 保有ポジションに建玉の方向を返す()
    {
        var view = Create(fills:
            [Fill(TradeSide.Sell, 10, 90m, "SHRT"), Fill(TradeSide.Buy, 5, 280m, "LONG")]).Build();

        view.Positions.Should().HaveCount(2);
        view.Positions.Single(p => p.Symbol == "SHRT").Side.Should().Be(TradeSide.Sell);
        view.Positions.Single(p => p.Symbol == "LONG").Side.Should().Be(TradeSide.Buy);
    }

    // T-340-12: **借株料の累計は供給元がコードに存在しない。** 0 を返すと「費用が発生していない」と読める。
    [Fact]
    public void 借株料の累計は未供給として宣言し0を返さない()
    {
        var view = Create(fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT")]).Build();

        view.BorrowFeeAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.TotalAccruedBorrowFeeUsd.Should().BeNull();
        view.Positions.Single().BorrowFeeAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.Positions.Single().AccruedBorrowFeeUsd.Should().BeNull();
    }

    // ---- 維持率割れによる自動縮小の発動履歴 ----

    // T-340-13: 発動履歴を**記録する経路がコードに存在しない**（履歴ストアも照会 API も無い）。
    // 空列＝「発動なし」と区別する（区別しないと「統制が働いていて発動が無かった」と読める）。
    [Fact]
    public void 発動履歴は維持率の供給が無い間は未供給として宣言する()
    {
        var view = Create(snapshot: null).Build();

        view.ReductionHistoryAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.ReductionHistory.Should().BeEmpty();
    }

    // T-340-13b（**潜在的な fail-open の固定**）: 維持率の供給が入っても、発動履歴は未供給のままである。
    //
    // 当初の実装は供給可否を `snapshot is null` に結び付けており、**維持率の供給が入った瞬間
    // （#330 / #331 が実装された日）に `NotApplicable`＝「概念が成立しない・正常」へ化けた**。
    // 記録経路が無いままの空列が「統制が働いていて発動が無かった」と読める向きであり、
    // しかも供給が入った日は誰も画面を見直さないため最も気づかれにくい。
    // **本テストは「維持率が供給されている」側を固定する**——落ちるのは記録経路が実装されたときであり、
    // そのとき本テストごと見直せばよい。
    [Fact]
    public void 発動履歴は維持率が供給されていても記録経路が無い限り未供給のままである()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };

        var view = Create(snapshot).Build();

        // 前提: 維持率そのものは供給されている（この分岐が実際に通っていることの確認）。
        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.Available);

        view.ReductionHistoryAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.ReductionHistory.Should().BeEmpty();
    }

    // ---- 強制買戻しの発生回数（FR-21・ADR-0016 決定15・#424・IADR-0162 決定2） ----
    //
    // **FR-21 は本実装から起こした要求である**（#459 の pin 前進で計画側に現れた。環流 planning#248・
    // 利用者裁定 2026-08-07 質問票 第15回 Q9）。「ブローカ建玉の観測が到達した事実を記録する」——
    // **推定経路の実装は発生回数の供給の必要条件であって十分条件ではない**、という本実装での実測が
    // そのまま要求になった。下の 2 本が FR-21 の受け入れ基準に対応する検査であり、**FR-21 という ID が
    // 計画に無かった時期に書かれたため起点 ID を持っていなかった**。pin 前進に合わせて付す。

    // T-10-257: **供給元が無いことを「未供給」として宣言する。**
    // 計画（05_screens SC-03 の供給元の表）は本項目へ **「0 件と表示してはならない」**と名指しで注記した。
    // #419 で推定台帳は入ったが、台帳は**推定が起きたときにしか行を書かない**ため、行数 0 は
    // 「観測が一度も届いていない」と「観測して 0 件だった」を区別できない。
    [Fact]
    public void 強制買戻しの発生回数は未供給として宣言し0件を返さない()
    {
        var view = Create(fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT")]).Build();

        view.BuyInCountAvailability.Should().Be(MetricAvailability.NotSupplied);
        // **0 を返さない**（0 は「強制買戻しは起きていない」と読める）。
        view.BuyInCount.Should().BeNull();
        view.BuyInCount.Should().NotBe(0);
    }

    // T-10-258（**潜在的な fail-open の固定**・IADR-0154 残余リスク4 と同型）:
    // **他の指標の供給状態に本項目を結び付けない。** 維持率・現在値・建玉の有無がどう変わっても、
    // 発生回数の供給可否は変わらない。結び付けると、維持率の供給が入った日（#331 / #342）に本項が
    // 黙って `Available` / `NotApplicable` へ化け、**観測経路が無いままの 0 件が「起きていない」と読める**。
    [Fact]
    public void 強制買戻しの発生回数は他の指標が供給されても未供給のままである()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };

        var view = Create(
            snapshot,
            fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT")],
            prices: new Dictionary<(string Symbol, Market Market), decimal>
            {
                [("SHRT", Market.UnitedStates)] = 80m,
            }).Build();

        // 前提: 維持率・空売り比率はいずれも供給されている（この分岐が実際に通っていることの確認）。
        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.Available);
        view.ShortExposureAvailability.Should().Be(MetricAvailability.Available);

        view.BuyInCountAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.BuyInCount.Should().BeNull();
    }

    // T-10-259: **建玉が 1 件も無いことは「対象なし」であって「未供給」ではない。**
    // 空売り比率は概念が成立しないだけであり運用上の異常ではない（打つ手が違う）。
    // 一方で維持率・借株料・発生回数は**建玉の有無と無関係に**供給元そのものが無い。
    [Fact]
    public void 建玉が無いときの空売り比率は対象なしであり未供給ではない()
    {
        var view = Create().Build();

        view.ShortExposureAvailability.Should().Be(MetricAvailability.NotApplicable);
        view.ShortExposureAvailability.Should().NotBe(MetricAvailability.NotSupplied);
        view.ShortExposureRatio.Should().BeNull();
        // 同じ「建玉なし」でも、供給元が無い項目は未供給のままである（1 つの状態へ潰さない）。
        view.BorrowFeeAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.BuyInCountAvailability.Should().Be(MetricAvailability.NotSupplied);
    }

    // T-10-260: **正当な 0 を未供給へ倒さない**（逆方向の否定形）。
    // 空売り建玉が 1 件も無く現物だけを持つ口座では、空売り比率は**測定できて 0** である。
    // これを「未供給」と宣言すると、供給されているのに「取得できていません」と嘘をつく——
    // 3 状態の区別は**両方向**に守らなければ意味を持たない。
    [Fact]
    public void 空売り建玉が無い口座の空売り比率は供給ありの0である()
    {
        var view = Create(
            fills: [Fill(TradeSide.Buy, 5, 100m, "LONG")],
            prices: new Dictionary<(string Symbol, Market Market), decimal>
            {
                [("LONG", Market.UnitedStates)] = 100m,
            }).Build();

        view.ShortExposureAvailability.Should().Be(MetricAvailability.Available);
        view.ShortExposureRatio.Should().Be(0m);
    }

    // ---- 強制買戻しの発生回数の供給（FR-21・#470・IADR-0186） ----
    //
    // 🔴 **FR-21 の規約は両方向に効く。** 未供給を 0 に見せないことと、**正当な 0 を未供給に見せない**ことの
    // 両方を固定する。上の T-10-257 / T-10-258 が前者、以下が後者を担う。
    //
    // 期間は**当月（月初〜当日）**である（IADR-0186 決定1。ADR-0016 決定15 が「発生回数」を月報＝当月へ、
    // 「発生有無」を日報＝当日へ割り当てているため）。

    // 当月の営業日をすべて観測済みにする（＝被覆が成立する状態）。
    private static InMemoryPositionObservationArrivalStore FullyObservedMonth(DateOnly today)
    {
        var store = new InMemoryPositionObservationArrivalStore();
        var calendar = new WeekendBusinessCalendar();
        for (var day = new DateOnly(today.Year, today.Month, 1); day <= today; day = day.AddDays(1))
        {
            if (calendar.IsBusinessDay(day))
            {
                store.Record(day, new DateTimeOffset(day.Year, day.Month, day.Day, 3, 0, 0, TimeSpan.Zero));
            }
        }

        return store;
    }

    // T-10-278（**本 issue の主目的**）: FR-21 —— **正当な 0 を未供給に見せない。**
    // 当月が観測で覆われており推定が 1 件も無いなら、それは**観測した結果の 0** である。
    [Fact]
    public void 当月が観測で覆われ推定0件なら供給ありの0を返す()
    {
        var view = Create(observationArrivals: FullyObservedMonth(Today)).Build();

        view.BuyInCountAvailability.Should().Be(MetricAvailability.Available);
        view.BuyInCount.Should().Be(0);
    }

    // T-10-279: 被覆が成立していれば件数をそのまま返す（集計元は推定台帳＝ADR-0016 決定15）。
    [Fact]
    public void 当月が観測で覆われていれば推定件数を供給する()
    {
        var inferences = new InMemoryBuyInInferenceStore();
        inferences.Append(BuyIn("AAPL", new DateOnly(2026, 8, 4)));
        inferences.Append(BuyIn("GME", new DateOnly(2026, 8, 5)));

        var view = Create(
            observationArrivals: FullyObservedMonth(Today),
            buyInInferences: inferences).Build();

        view.BuyInCountAvailability.Should().Be(MetricAvailability.Available);
        view.BuyInCount.Should().Be(2);
    }

    // T-10-280（**否定形**）: 観測が 1 日も届いていなければ未供給のままである（0 件と描かない）。
    // 推定台帳が空であることと、観測が届いていないことは**別の事実**である。
    [Fact]
    public void 観測が一度も届いていなければ未供給のままである()
    {
        var view = Create(observationArrivals: new InMemoryPositionObservationArrivalStore()).Build();

        view.BuyInCountAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.BuyInCount.Should().BeNull();
    }

    // T-10-281（**否定形・最重要**）: **1 日でも欠ければ未供給へ倒す。**
    // 部分的な観測を「覆っている」と扱うと、観測が止まっていた日の推定 0 件が正当な 0 として画面に出る
    // （FR-21 が塞いだ失敗モード 2＝観測が途中で止まった期間）。
    [Fact]
    public void 当月の営業日が一日でも欠ければ未供給へ倒す()
    {
        var store = FullyObservedMonth(Today);
        var partial = new InMemoryPositionObservationArrivalStore();
        foreach (var day in store.GetObservedDaysBetween(new DateOnly(2026, 8, 1), Today))
        {
            // 8/4 だけ落とす（＝その日は観測が届かなかった）。
            if (day != new DateOnly(2026, 8, 4))
            {
                partial.Record(day, new DateTimeOffset(day.Year, day.Month, day.Day, 3, 0, 0, TimeSpan.Zero));
            }
        }

        var view = Create(observationArrivals: partial).Build();

        view.BuyInCountAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.BuyInCount.Should().BeNull();
    }

    // T-10-282（**否定形**・IADR-0154 残余リスク4）: **他の指標の供給状態を条件に混ぜない。**
    // 維持率・現在値が供給されていても、本項の可否は観測の到達だけで決まる（両方向で確認する）。
    [Fact]
    public void 強制買戻しの発生回数は他の指標の供給状態に影響されない()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };
        var prices = new Dictionary<(string Symbol, Market Market), decimal>
        {
            [("SHRT", Market.UnitedStates)] = 80m,
        };

        // 他の指標が供給されていても、観測が無ければ未供給のまま（T-10-258 と同じ向き）。
        var withoutObservation = Create(
            snapshot, fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT")], prices: prices).Build();
        withoutObservation.MaintenanceMarginAvailability.Should().Be(MetricAvailability.Available);
        withoutObservation.BuyInCountAvailability.Should().Be(MetricAvailability.NotSupplied);

        // 逆に、他の指標が**未供給**でも、観測が覆っていれば本項は供給される。
        var withObservation = Create(observationArrivals: FullyObservedMonth(Today)).Build();
        withObservation.MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
        withObservation.BuyInCountAvailability.Should().Be(MetricAvailability.Available);
    }

    // T-10-283（**否定形・構造**・IADR-0186 決定4 / IADR-0163 決定2 と同型）:
    // **観測ストアと推定台帳は必須依存である。** 省略可能引数へ戻すと配線を落としても既定値で緑のまま通る
    //（「緑だが検査されていない」）。`currentPrices` は**省略可能のまま**であることも同時に固定する
    //（供給が無いことが正当な状態であり、`null` の意味が違う）。
    [Fact]
    public void 観測ストアと推定台帳はSC03集約の必須依存である()
    {
        var parameters = typeof(ShortSellingStatusService).GetConstructors().Single().GetParameters();

        var required = parameters
            .Where(p => !p.IsOptional)
            .Select(p => p.ParameterType)
            .ToList();

        required.Should().Contain(typeof(IPositionObservationArrivalStore));
        required.Should().Contain(typeof(IBuyInInferenceStore));

        parameters.Single(p => p.ParameterType == typeof(ICurrentPriceSource)).IsOptional
            .Should().BeTrue("現在値の供給が無いことは正当な状態である（null の意味が違う）");
    }

    // 推定 1 件（リセット行ではない＝発生回数に数える。NewlyInferredQuantity > 0）。
    private static BuyInInferenceRecord BuyIn(string symbol, DateOnly inferredOn)
    {
        var at = new DateTimeOffset(inferredOn.Year, inferredOn.Month, inferredOn.Day, 3, 0, 0, TimeSpan.Zero);
        return new BuyInInferenceRecord(
            Guid.NewGuid(), symbol, Market.UnitedStates,
            LedgerShortQuantity: 10, BrokerShortQuantity: 0, InFlightCloseQuantity: 0,
            UnexplainedQuantity: 10, NewlyInferredQuantity: 10,
            BanUntil: inferredOn.AddDays(30), InferredOn: inferredOn, ObservedAt: at, InferredAt: at);
    }
}
