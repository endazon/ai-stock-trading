using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10, UC-06, ADR-0016, ADR-0009, #329 第 2 段階, IADR-0131:
// 空売り専用統制 8 規則（1 銘柄 equity の 10% / 逆指値必須 / 借株料 年率 20% / 維持率 40% と規制要求の
// 厳しい方 / 株価 $5.00 / 空売り比率 50% / 権利確定日前日 / 強制買戻し 30 日禁止）の網羅。
//
// テスト戦略（docs/tests/README.md §2）の 3 点セットで構成する。
//   1. 境界値テーブル — 閾値の直下 / 一致 / 直上
//   2. プロパティベース — 入力によらず成り立つ不変条件（厳しい方が効く・equity に比例する）
//   3. 否定形 — 統制を**迂回できない**こと（照会不能・逆指値なし・分割発注・別市場・手仕舞い経路）
//
// 閾値はマジックナンバーで書かず統制設定（ShortSellingLimits）から引く。設定値そのものが計画どおりで
// あることは AiStockTrading.PlanConformance.Tests が計画書と突き合わせて別途保証する。
public class ShortSellingControlsTests
{
    private const decimal Equity = 100_000m;
    private static readonly DateOnly Today = new(2026, 8, 4);

    private static ShortSellingLimits Limits => TradingDefaults.CreateShortSellSettings().Limits;

    // 空売りを有効にした設定。#332・IADR-0132 決定2 により、空売りの有効・無効は
    // 取引ガードの商品種別（3 値）が単一情報源である（専用フラグ ShortSellSettings.Enabled は廃止した）。
    private static RiskManagementSettings Settings(bool shortSellEnabled = true)
    {
        var settings = TradingDefaults.CreateSettings();
        var productTypes = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong };
        if (shortSellEnabled)
        {
            productTypes.Add(ProductType.ShortSell);
        }

        return settings with
        {
            Guard = settings.Guard with { EnabledProductTypes = productTypes },
        };
    }

    private static PortfolioSnapshot Snapshot(decimal equity = Equity) =>
        new() { Capital = equity };

    // 名目額を指定した空売り（新規売り建て）。数量 1・株価 ＝ 名目額とする（株価下限 $5.00 は満たす）。
    // 逆指値（StopLossPrice）を同時に持つのが既定の姿である（ADR-0016 決定2(b)）。
    private static OrderIntent ShortEntry(decimal notional) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            Quantity: 1, Price: notional, PositionEffect.Open, StopLossPrice: notional * 1.1m);

    // 株価を明示した空売り（株価下限・維持率の判定用）。数量で名目額を作る。
    private static OrderIntent ShortEntryAt(
        decimal price,
        int quantity = 1,
        decimal? stopLossPrice = 1_000m,
        Market market = Market.UnitedStates) =>
        new("AAPL", market, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            quantity, price, PositionEffect.Open, stopLossPrice);

    private static ShortSellOrderContext Context(
        decimal? borrowRateAnnual = 0.10m,
        bool shortPermit = true,
        DateOnly? buyInBanUntil = null,
        DateOnly? dividendRecordDate = null,
        decimal? maintenanceMarginRatio = 1.00m,
        decimal symbolShortExposure = 0m,
        decimal totalShortExposure = 0m,
        // 既定ではロング建玉を equity 相当だけ持つ。空売り比率 50%（ADR-0016 決定9）は
        // **ロングが無ければ 1 件目の空売りで既に 100%** となり常に成立してしまうため、
        // 他の規則の境界を見るテストでは比率が先に効かない状態を既定とする。
        decimal totalExposure = Equity) =>
        new()
        {
            Today = Today,
            BorrowRateAnnual = borrowRateAnnual,
            ShortPermit = shortPermit,
            BuyInBanUntil = buyInBanUntil,
            DividendRecordDate = dividendRecordDate,
            MarginSnapshot = MarginSnapshot(maintenanceMarginRatio),
            SymbolShortExposure = symbolShortExposure,
            TotalShortExposure = totalShortExposure,
            TotalExposure = totalExposure,
        };

    // FR-10, ADR-0016 決定7（2026-08-07 追記）, #420, IADR-0160: 維持率は**束**（純資産と信用建玉）として
    // 供給される。指定された維持率をちょうど生む最小の束を組み立てる（$100 の空売り建玉 1 件＝閾値 40%
    // であり、株価 $12.50 以上の注文に対して口座側が余分に厳しくならない）。
    // **null は「供給が無い」**を意味し、0 件の束（＝観測した結果 0 だった、と読める値）を作らない。
    private const decimal SnapshotPositionPriceUsd = 100m;
    private const int SnapshotPositionQuantity = 100;

    private static MaintenanceMarginSnapshot? MarginSnapshot(decimal? ratio) =>
        ratio is { } r
            ? new MaintenanceMarginSnapshot
            {
                NetEquityUsd = r * SnapshotPositionPriceUsd * SnapshotPositionQuantity,
                Positions =
                [
                    new MarginPosition
                    {
                        Symbol = "AAPL",
                        Market = Market.UnitedStates,
                        Side = TradeSide.Sell,
                        ProductType = ProductType.ShortSell,
                        Quantity = SnapshotPositionQuantity,
                        PriceUsd = SnapshotPositionPriceUsd,
                        RequiredMarginUsd = 4_000m,
                    },
                ],
            }
            : null;

    private static OrderScreeningResult Evaluate(
        OrderIntent intent,
        ShortSellOrderContext? context,
        RiskManagementSettings? settings = null,
        decimal equity = Equity) =>
        RiskEvaluator.Evaluate(intent, settings ?? Settings(), Snapshot(equity), null, context);

    // ------------------------------------------------------------------
    // 1. 境界値テーブル
    // ------------------------------------------------------------------

    // FR-10 (1), ADR-0016 決定2(a), §5: 1 銘柄あたりの空売り建玉は equity の 10%（$3,000 で $300）。
    [Theory]
    [InlineData(-1, false)] // 直下
    [InlineData(0, false)]  // 一致（「上限とする」＝ちょうどは許容）
    [InlineData(+1, true)]  // 直上
    public void 一銘柄あたり空売り上限は境界で切り替わる(int delta, bool expectedRejected)
    {
        var cap = Limits.PerSymbolCapFor(Equity);

        var result = Evaluate(ShortEntry(cap + delta), Context());

        result.Reasons.Contains(RejectionReason.ShortExposureExceeded).Should().Be(expectedRejected);
    }

    // FR-10 (3), ADR-0016 決定3: 借株料が年率 20% を**超える**銘柄への空売りは拒否する。
    [Theory]
    [InlineData(-0.01, false)]
    [InlineData(0, false)]
    [InlineData(+0.01, true)]
    public void 借株料上限は境界で切り替わる(decimal delta, bool expectedRejected)
    {
        var rate = Limits.BorrowRateCapAnnual + delta;

        var result = Evaluate(ShortEntry(1_000m), Context(borrowRateAnnual: rate));

        result.Reasons.Contains(RejectionReason.BorrowCostExceeded).Should().Be(expectedRejected);
    }

    // FR-10 (5), ADR-0016 決定7: 株価 $5.00 **未満**は空売りの対象外（$5.00 ちょうどは対象）。
    [Theory]
    [InlineData(-0.01, true)]
    [InlineData(0, false)]
    [InlineData(+0.01, false)]
    public void 空売りの株価下限は境界で切り替わる(decimal delta, bool expectedRejected)
    {
        var price = Limits.PriceFloorUsd + delta;

        var result = Evaluate(ShortEntryAt(price), Context());

        result.Reasons.Contains(RejectionReason.ShortPriceFloorBreach).Should().Be(expectedRejected);
    }

    // FR-10 (6), ADR-0016 決定9: 空売り建玉の合計は建玉総額の 50% を**超えない**。
    // 既存のロング建玉 10,000 に対し、空売り n を足した後の比率 n / (10,000 + n) が 50% を超えるかで判定する
    //（n = 10,000 でちょうど 50%）。1 銘柄あたり上限が先に効かないよう equity を大きく取る。
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(+1, true)]
    public void 空売り比率の上限は境界で切り替わる(int delta, bool expectedRejected)
    {
        const decimal LongExposure = 10_000m;
        // n / (LongExposure + n) = cap を満たす n（cap = 0.50 なら n = LongExposure）。
        var boundary = LongExposure * Limits.ExposureRatioCap / (1m - Limits.ExposureRatioCap);
        var notional = boundary + delta;

        var result = Evaluate(
            ShortEntry(notional),
            Context(totalShortExposure: 0m, totalExposure: LongExposure),
            equity: 10_000_000m);

        result.Reasons.Contains(RejectionReason.ShortExposureExceeded).Should().Be(expectedRejected);
    }

    // FR-10 (6), ADR-0016 決定9: 比率は**建玉総額に対する**ものであるため、ロング建玉が無ければ
    // 1 件目の空売りで既に 100% となり成立しない（＝空売りだけの建玉構成は作れない）。
    // 決定9 の趣旨（ロングとショートの混在で方向性リスクを部分的に相殺する）どおりの帰結である。
    [Fact]
    public void ロング建玉が無ければ空売り比率の上限に掛かる()
    {
        var result = Evaluate(ShortEntry(1_000m), Context(totalShortExposure: 0m, totalExposure: 0m));

        result.Reasons.Should().Contain(RejectionReason.ShortExposureExceeded);
    }

    // FR-10 (6), ADR-0016 決定9, #329 第 3 段階（プロパティベース）: 比率 50% の判定は
    // **「空売り建玉の合計がロング建玉の総額を超えない」と等価**である。
    //   (S + N) ≦ (L + S + N) × 0.5  ⇔  2(S + N) ≦ L + (S + N)  ⇔  S + N ≦ L
    // ロング建玉 L が空売り枠の上限そのものになる（equity ではない）ことを、建玉構成によらず固定する。
    // この等価形と、そこから導かれる含意（L = 0 では空売りを開始できない・Stage 1 で空売り単独の
    // 検証ができない）は計画へ環流済みである（feedback/20260804_adr0016-short-ratio-denominator.md）。
    [Theory]
    [InlineData(10_000, 0, 9_999, false)]      // L のみ・枠内
    [InlineData(10_000, 0, 10_000, false)]     // S + N = L（境界・許容）
    [InlineData(10_000, 0, 10_001, true)]      // S + N > L
    [InlineData(10_000, 4_000, 6_000, false)]  // 既存ショートがある場合も同じ式
    [InlineData(10_000, 4_000, 6_001, true)]
    [InlineData(0, 0, 1, true)]                // L = 0 なら 1 件目から通らない
    public void 空売り比率の判定は空売り合計がロング建玉総額を超えないことと等価である(
        decimal longExposure, decimal shortExposure, decimal notional, bool expectedRejected)
    {
        Limits.ExposureRatioCap.Should().Be(0.50m, "この等価形は比率上限 50% に固有である");
        (shortExposure + notional > longExposure).Should().Be(
            expectedRejected, "等価形 S + N ≦ L で表した期待値");

        var result = Evaluate(
            ShortEntry(notional),
            Context(totalShortExposure: shortExposure, totalExposure: longExposure + shortExposure),
            // 1 銘柄あたり上限（equity の 10%）が先に効かないよう equity を大きく取る。
            equity: 10_000_000m);

        result.Reasons.Contains(RejectionReason.ShortExposureExceeded).Should().Be(expectedRejected);
    }

    // FR-10 (4), ADR-0016 決定7: 維持率の閾値は「40%」と規制要求 max($5.00 ÷ 株価, 30%) の**厳しい方**。
    // 両者が入れ替わるのは株価 **$12.50**（= $5.00 ÷ 40%）である。
    // **$16.67 と混同しないこと**（$16.67 は規制側の内訳が固定額から時価 30% へ替わる点であり別の境界）。
    [Theory]
    [InlineData(-0.01, true)]  // $12.49: 規制側（40% 超）が効く
    [InlineData(0, false)]     // $12.50: 一致（どちらでも 40%）
    [InlineData(+0.01, false)] // $12.51: 自前の 40% が効く
    public void 維持率閾値は株価12ドル50セントを境に規制側と自前側が入れ替わる(
        decimal delta, bool expectedRegulatoryStricter)
    {
        var limits = Limits;
        var crossover = ShortSellingLimits.RegulatoryFixedMaintenancePerShareUsd
            / limits.MaintenanceMarginThreshold;
        var price = crossover + delta;

        var threshold = limits.MaintenanceMarginThresholdFor(price);
        var regulatory = ShortSellingLimits.RegulatoryMaintenanceMarginFor(price);

        threshold.Should().Be(Math.Max(limits.MaintenanceMarginThreshold, regulatory));
        (regulatory > limits.MaintenanceMarginThreshold).Should().Be(expectedRegulatoryStricter);
        threshold.Should().Be(
            expectedRegulatoryStricter ? regulatory : limits.MaintenanceMarginThreshold);
    }

    // FR-10 (4), ADR-0016 決定7: 維持率が適用閾値を**割り込む**なら新規空売りを拒否する。
    [Theory]
    [InlineData(-0.0001, true)]
    [InlineData(0, false)]
    [InlineData(+0.0001, false)]
    public void 維持率は適用閾値の境界で切り替わる(decimal delta, bool expectedRejected)
    {
        const decimal Price = 100m; // $12.50 超のため自前の 40% が効く
        var threshold = Limits.MaintenanceMarginThresholdFor(Price);

        var result = Evaluate(
            ShortEntryAt(Price, quantity: 10),
            Context(maintenanceMarginRatio: threshold + delta));

        result.Reasons.Contains(RejectionReason.MaintenanceMarginBreach).Should().Be(expectedRejected);
    }

    // FR-10 (7), ADR-0016 決定5: 権利確定日の**前日**は当該銘柄の新規空売りを禁止する。
    [Theory]
    [InlineData(-2, false)] // 前々日
    [InlineData(-1, true)]  // 前日
    [InlineData(0, false)]  // 当日
    [InlineData(+1, false)] // 翌日
    public void 権利確定日前日のみ新規空売りを禁止する(int offsetFromRecordDate, bool expectedRejected)
    {
        var recordDate = Today.AddDays(-offsetFromRecordDate);

        var result = Evaluate(ShortEntry(1_000m), Context(dividendRecordDate: recordDate));

        result.Reasons.Contains(RejectionReason.DividendRecordDateNear).Should().Be(expectedRejected);
    }

    // FR-10 (8), ADR-0016 決定4/決定10: 強制買戻し（buy-in）を検知した銘柄は **30 日間**空売りを禁止する。
    // 理由コードは専用の BuyInBanned である（2026-08-04 の計画改訂・#374）。
    [Theory]
    [InlineData(0, true)]   // 検知当日
    [InlineData(29, true)]  // 29 日後（禁止中）
    [InlineData(30, false)] // 30 日後（解除）
    [InlineData(31, false)]
    public void 強制買戻し検知銘柄は30日間空売りできない(int daysSinceDetection, bool expectedRejected)
    {
        var detectedOn = Today.AddDays(-daysSinceDetection);
        var banUntil = Limits.BuyInBanUntil(detectedOn);

        var result = Evaluate(ShortEntry(1_000m), Context(buyInBanUntil: banUntil));

        result.Reasons.Contains(RejectionReason.BuyInBanned).Should().Be(expectedRejected);
    }

    // ------------------------------------------------------------------
    // 2. プロパティベース（入力によらず成り立つ不変条件）
    // ------------------------------------------------------------------

    // FR-10「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」・§5 注記
    //「空売りは 1 銘柄あたり equity の 10% が本上限（1 注文 25%）より厳しいため、空売り注文には 10% が適用される」。
    // 同じ名目額でも、買いは 25% まで通り、空売りは 10% で止まる。
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.10)]
    [InlineData(0.15)]
    [InlineData(0.25)]
    [InlineData(0.30)]
    public void 空売りには1注文上限より厳しい1銘柄上限が効く(decimal ratioOfEquity)
    {
        var settings = Settings();
        var notional = Equity * ratioOfEquity;
        var shortCap = settings.ShortSell.Limits.PerSymbolCapFor(Equity);
        var perOrderCap = settings.Limits.MaxOrderAmountFor(Equity);

        var shortResult = Evaluate(ShortEntry(notional), Context(), settings);
        var buyResult = RiskEvaluator.Evaluate(
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.MarginLong,
                BrokerProvider.InternalPaper, 1, notional, PositionEffect.Open),
            settings,
            Snapshot());

        shortResult.IsApproved.Should().Be(
            notional <= shortCap, "空売りは 1 銘柄あたり {0} までしか通らない", shortCap);
        buyResult.IsApproved.Should().Be(
            notional <= perOrderCap, "買いは 1 注文あたり {0} まで通る", perOrderCap);
    }

    // FR-10, IADR-0130 決定1 と同じ不変条件: equity を k 倍すると空売りの 1 銘柄あたり上限も k 倍になる
    //（金額で表す統制は固定額ではなく equity 比で保持する）。
    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1_700)]
    public void equityをk倍すると1銘柄あたり空売り上限もk倍になる(decimal k)
    {
        var limits = Limits;

        limits.PerSymbolCapFor(Equity * k).Should().Be(limits.PerSymbolCapFor(Equity) * k);
    }

    // ADR-0016 決定7: 適用される維持率閾値は、株価によらず**自前 40% と規制要求の大きい方**である。
    // 回復目標（自動縮小の目標。#330 が使う）は常に「適用閾値 + 5 ポイント」であり閾値に連動する。
    [Theory]
    [InlineData(5.00)]
    [InlineData(9.99)]
    [InlineData(12.50)]
    [InlineData(16.67)]
    [InlineData(100.00)]
    [InlineData(3_000.00)]
    public void 維持率閾値は常に自前と規制要求の厳しい方であり回復目標は閾値に連動する(decimal price)
    {
        var limits = Limits;
        var regulatory = ShortSellingLimits.RegulatoryMaintenanceMarginFor(price);

        var threshold = limits.MaintenanceMarginThresholdFor(price);

        threshold.Should().BeGreaterThanOrEqualTo(limits.MaintenanceMarginThreshold);
        threshold.Should().BeGreaterThanOrEqualTo(regulatory);
        threshold.Should().Be(Math.Max(limits.MaintenanceMarginThreshold, regulatory));
        limits.MaintenanceRecoveryTargetFor(price).Should()
            .Be(threshold + limits.MaintenanceRecoveryTargetOffset);
        // 規制側は 30% を下回らない（FINRA Rule 4210(c) の下限）。
        regulatory.Should().BeGreaterThanOrEqualTo(ShortSellingLimits.RegulatoryMaintenanceMarginFloor);
    }

    // ADR-0016 決定10: 空売りの拒否理由 9 種は**すべてクラス A**であり、
    // 「統制違反 0 件」（クラス C 限定）の件数に影響しない（2026-08-04 に 7 種から改訂。#374）。
    [Theory]
    [InlineData(RejectionReason.ShortSellDisabled)]
    [InlineData(RejectionReason.StopOrderRequired)]
    [InlineData(RejectionReason.BorrowUnavailable)]
    [InlineData(RejectionReason.BorrowCostExceeded)]
    [InlineData(RejectionReason.BuyInBanned)]
    [InlineData(RejectionReason.ShortExposureExceeded)]
    [InlineData(RejectionReason.MaintenanceMarginBreach)]
    [InlineData(RejectionReason.DividendRecordDateNear)]
    [InlineData(RejectionReason.ShortPriceFloorBreach)]
    public void 空売りの拒否理由はいずれもクラスAであり統制違反に計上しない(RejectionReason reason)
    {
        RejectionReasonClassification.ClassOf(reason).Should().Be(RejectionReasonClass.A);
        RejectionReasonClassification.CountsAsControlViolation(reason).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 3. 否定形（統制を迂回できないこと）
    // ------------------------------------------------------------------

    // T-10-210（**最重要**）: FR-10 (3), ADR-0016 決定3（2026-08-06 改訂）, IADR-0158 決定1, #417
    // ——**一次ゲートは借株可否（IsShortPermit）である。**
    // 借株が許可されない銘柄は、**借株料が上限内であっても**空売りできない。
    // 実測では料率が一律（在庫が 20 倍以上開いても 1.5）であり、料率の閾値では危険な銘柄を弾けない。
    // 弾いているのは可否の側である（AMC・SPCE が IsShortPermit=False / 在庫 0）。
    // **本判定を外すと、借株できない銘柄の空売りが素通りする。**
    [Theory]
    [InlineData(0.01)]  // 上限（20%）を大きく下回る料率でも通さない
    [InlineData(0.10)]  // 既定の文脈と同じ料率
    public void 借株が許可されない銘柄は借株料が上限内でも空売りできない(decimal borrowRateAnnual)
    {
        borrowRateAnnual.Should().BeLessThan(
            Limits.BorrowRateCapAnnual,
            "料率側の統制（BorrowCostExceeded）が働いて緑になっては、一次ゲートを検証したことにならない");

        var result = Evaluate(ShortEntry(1_000m), Context(shortPermit: false, borrowRateAnnual: borrowRateAnnual));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(
            RejectionReason.BorrowUnavailable,
            "IsShortPermit=False（借株在庫 0・Reg SHO 由来の制限）は「都度の借株需給による locate 失敗」"
                + "そのものであり、既存の BorrowUnavailable へ写像する（新しいコードは追加しない）");
        // 一次ゲートの拒否は**クラス A**（統制の正常作動）であり、「統制違反 0 件」（クラス C 限定）へ
        // 混入させない。混入させると段階昇格ゲート（FR-20）が市況由来の事象で不合格になる。
        RejectionReasonClassification.CountsAsControlViolation(result.Reasons).Should().BeFalse();
    }

    // T-10-211: 同上の裏面——**可否と料率を混ぜない**。借株できない銘柄に「料率が高い」も併記すると、
    // 監査ログ（FR-11）の理由が実態より多弁になり、原因（可否 or 料率）の切り分けが濁る。
    [Fact]
    public void 借株が許可されない銘柄の拒否理由に借株料超過は混ざらない()
    {
        var rate = Limits.BorrowRateCapAnnual + 0.10m; // 可否・料率のいずれもが拒否側の入力

        var result = Evaluate(ShortEntry(1_000m), Context(shortPermit: false, borrowRateAnnual: rate));

        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable);
        result.Reasons.Should().NotContain(RejectionReason.BorrowCostExceeded);
    }

    // T-10-212: FR-10 (3), ADR-0016 決定3 改訂・IADR-0158 決定2 ——
    // **20% の閾値判定は「発火しない既知の統制」として残置する。**
    // 実測の情報源（moomoo の一律料率）では発火しない見込みだが、**「発火しない」ことと「無い」ことは別**であり、
    // 落とすと料率が銘柄別になった日に無防備になる。**残置していることを本テストが固定する**
    //（境界の切り替わりは「借株料上限は境界で切り替わる」が別に固定している）。
    [Fact]
    public void 借株料の閾値判定は発火しない既知の統制として残置される()
    {
        var permitted = Context(shortPermit: true, borrowRateAnnual: Limits.BorrowRateCapAnnual + 0.01m);

        var result = Evaluate(ShortEntry(1_000m), permitted);

        result.Reasons.Should().Contain(
            RejectionReason.BorrowCostExceeded,
            "一次ゲートを可否へ移した後も、上限超の料率が供給されれば閾値判定は働かなければならない");
        RejectionReasonClassification.ClassOf(RejectionReason.BorrowCostExceeded)
            .Should().Be(RejectionReasonClass.A);
    }

    // T-10-213: **否定形（単位の未確定）**: 実測の `ShortFeeRate = 1.5` を**そのまま年率として**写像すると、
    // 上限 0.20 を 7.5 倍超過して**全銘柄が拒否される**——「一律料率だから閾値は発火しない」という
    // 決定3 改訂の記述と正反対の結果になる。**同じ値が単位の読み方ひとつで「何も弾かない」から
    // 「全部弾く」へ反転する**ため、単位が確定するまで `BorrowRateAnnual` へ写像してはならない
    //（IADR-0158 決定3。現状は写像そのものを実装していない）。
    [Fact]
    public void 実測の借株料をそのまま年率として写像すると全件拒否になる()
    {
        const decimal observedShortFeeRate = 1.5m; // moomoo TrdGetMarginRatio.ShortFeeRate（単位未確定）

        var result = Evaluate(ShortEntry(1_000m), Context(borrowRateAnnual: observedShortFeeRate));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BorrowCostExceeded);
        // 1.5% の意味（0.015）なら発火しない。**読み方の違いで結論が反転する**ことを併せて固定する。
        Evaluate(ShortEntry(1_000m), Context(borrowRateAnnual: observedShortFeeRate / 100m))
            .Reasons.Should().NotContain(RejectionReason.BorrowCostExceeded);
    }

    // FR-10 (3), ADR-0016 決定3:「発注前に借株料を照会できない場合、空売り自体を行わない」。
    // **照会できないなら通す**に倒すと、年率 100% の銘柄でも発注が通る穴が残る（フェイルクローズ）。
    // 決定3 の 2026-08-06 改訂は**一次ゲートを可否へ移しただけ**であり、本縮退は残る。
    [Fact]
    public void 借株料を照会できないなら空売りは通らない()
    {
        var result = Evaluate(ShortEntry(1_000m), Context(borrowRateAnnual: null));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable);
    }

    // 同上。借株の照会経路そのものが無い（文脈が供給されない）場合も空売りは通らない。
    // 供給元の実装状況（#342 の PoC 待ち）に関わらず、既定の縮退は「空売りしない」側である。
    [Fact]
    public void 借株照会の経路が無いなら空売りは通らない()
    {
        var result = Evaluate(ShortEntry(1_000m), context: null);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable);
    }

    // FR-10 (2), ADR-0016 決定2(b): 逆指値（ストップ注文）を建玉と同時に必ず発注する。
    // 未約定・未受理であれば建玉を持たない。損切り価格を伴わない空売りは通さない。
    [Fact]
    public void 逆指値を伴わない空売りは通らない()
    {
        var result = Evaluate(ShortEntryAt(100m, stopLossPrice: null), Context());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.StopOrderRequired);
    }

    // FR-10 (5), ADR-0016 決定7/決定10: $4.99 は空売りの対象外。
    // **禁止銘柄（BannedSymbol・クラス C）で表現してはならない**——市況由来の事象を
    //「AI が禁止事項を犯そうとした件数」に混ぜると段階昇格ゲートが機能しなくなる。
    [Fact]
    public void 株価5ドル未満の空売りは通らずクラスCにも計上されない()
    {
        var result = Evaluate(ShortEntryAt(4.99m, quantity: 100), Context());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ShortPriceFloorBreach);
        result.Reasons.Should().NotContain(RejectionReason.BannedSymbol);
        RejectionReasonClassification.CountsAsControlViolation(result.Reasons).Should().BeFalse();
    }

    // FR-10 (8), ADR-0016 決定4: 強制買戻し由来の 30 日禁止も同様にクラス C へ混ぜない
    //（借株需給の逼迫という市況由来の事象であるため）。
    [Fact]
    public void 強制買戻しの禁止期間中は通らずクラスCにも計上されない()
    {
        var result = Evaluate(
            ShortEntry(1_000m), Context(buyInBanUntil: Limits.BuyInBanUntil(Today)));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BuyInBanned);
        result.Reasons.Should().NotContain(RejectionReason.BannedSymbol);
        RejectionReasonClassification.CountsAsControlViolation(result.Reasons).Should().BeFalse();
    }

    // **否定形**: FR-10 (8), ADR-0016 決定10（2026-08-04 追記）
    //「**BuyInBanned を BorrowUnavailable へ写像してはならない**」。
    // BorrowUnavailable は**都度の借株需給**による locate 失敗、BuyInBanned は**期間の経過**で解除される
    // 禁止状態であり、原因も解除条件も異なる。写像すると監査ログ（FR-11）の理由が実態と食い違い、
    // 日報・月報の「強制買戻しの発生有無・発生回数」（決定15）を拒否記録から復元できなくなる。
    // **借株が成立している（locate 済み・料率も上限内）状態で禁止期間だけが効く**ケースで見る。
    [Fact]
    public void 強制買戻しの禁止期間中の拒否は借株不可へ写像されない()
    {
        var result = Evaluate(
            ShortEntry(1_000m),
            Context(
                shortPermit: true,
                borrowRateAnnual: 0.05m,
                buyInBanUntil: Limits.BuyInBanUntil(Today)));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BuyInBanned);
        result.Reasons.Should().NotContain(
            RejectionReason.BorrowUnavailable,
            "禁止期間による拒否を借株不可へ写像すると、原因（期間の経過で解除される禁止 vs 都度の需給）も"
                + "解除条件も異なる 2 事象が監査ログ上で区別できなくなる（ADR-0016 決定10）");
        result.Reasons.Should().NotContain(RejectionReason.BorrowCostExceeded);
    }

    // **否定形**（上の裏面）: 借株不可（locate 失敗）を BuyInBanned へ寄せる逆向きの写像も塞ぐ。
    // 禁止期間が無い（`buyInBanUntil` が null）のに BuyInBanned が立てば、日報・月報の
    // 「強制買戻しの発生回数」が実際には起きていない事象で水増しされる。
    [Fact]
    public void 借株できないだけの拒否は強制買戻し禁止へ写像されない()
    {
        var result = Evaluate(ShortEntry(1_000m), Context(shortPermit: false));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable);
        result.Reasons.Should().NotContain(RejectionReason.BuyInBanned);
    }

    // FR-10, ADR-0016 決定1/8: 空売りは**既定で無効**（現物のみ）。実弾解禁は Stage 3 かつ自己資金 $5,000 以上。
    // 統制値が正しく設定されていても、有効化していない限り空売りは 1 件も通らない。
    [Fact]
    public void 空売りが無効なら他の条件をすべて満たしても通らない()
    {
        var result = Evaluate(ShortEntry(1_000m), Context(), Settings(shortSellEnabled: false));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ShortSellDisabled);
    }

    // ADR-0016 決定13: 空売りの対象市場は**米国株のみ**。株価下限は USD 建てであるため、
    // 別市場を許すと円建て株価（例 ¥300）が $5.00 の下限を素通りする（統制がまるごと無効化される）。
    [Fact]
    public void 米国株以外の空売りは株価下限を素通りせずに拒否される()
    {
        var lowPricedJapaneseStock = new OrderIntent(
            "6502", Market.Japan, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            Quantity: 10, Price: 300m, PositionEffect.Open, StopLossPrice: 330m);

        var result = Evaluate(lowPricedJapaneseStock, Context());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ShortSellDisabled);
        result.Reasons.Should().NotContain(RejectionReason.ShortPriceFloorBreach,
            "円建て株価を $5.00 と比較して「下限を満たした」と判定してはならない");
    }

    // FR-10 (1): 1 銘柄あたり上限は**累計**で効く。上限内の注文を分割して繰り返すことで
    // 上限を超える空売り建玉を作れない（既存の空売り建玉を判定に加える）。
    [Fact]
    public void 分割発注しても1銘柄あたり空売り上限は累計で効く()
    {
        var cap = Limits.PerSymbolCapFor(Equity);
        var half = cap / 2m;

        var first = Evaluate(ShortEntry(half), Context(symbolShortExposure: 0m));
        var second = Evaluate(ShortEntry(half), Context(symbolShortExposure: half));
        var third = Evaluate(ShortEntry(1m), Context(symbolShortExposure: cap));

        first.IsApproved.Should().BeTrue();
        second.IsApproved.Should().BeTrue("累計でちょうど上限までは通る");
        third.Reasons.Should().Contain(RejectionReason.ShortExposureExceeded, "累計が上限を超えたら止まる");
    }

    // FR-10 (4), IADR-0131 決定4: 維持率が取得できない状態で空売り建玉を積み増さない。
    //「取得できないなら判定しない」に倒すと、維持率を報告しないだけで統制を回避できる。
    [Fact]
    public void 維持率が取得できないのに空売り建玉があるなら積み増せない()
    {
        var result = Evaluate(
            ShortEntryAt(100m, quantity: 10),
            Context(maintenanceMarginRatio: null, totalShortExposure: 5_000m, totalExposure: 10_000m));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.MaintenanceMarginBreach);
    }

    // FR-10, ADR-0009, ADR-0003 の共通不変条件:「いずれの統制も新規建てのみを止め、
    // **手仕舞い（Close）と損切りは止めない**」。空売りの買い戻し（Buy + Close）は、株価が下限未満・
    // 逆指値なし・借株照会不能という**空売り統制がすべて成立する条件**でも通る。
    // 手仕舞いを止める統制を作ると、損失に上限が無い建玉を閉じられなくなる。
    [Fact]
    public void 空売りの買い戻しは空売り統制で止まらない()
    {
        var buyToCover = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.ShortSell, BrokerProvider.InternalPaper,
            Quantity: 1_000, Price: 1.00m, PositionEffect.Close);

        var result = Evaluate(buyToCover, context: null);

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // 4. 強制買戻し由来の 30 日禁止の**供給経路**（#419 / ADR-0016 決定4 の 2026-08-06 改訂 / IADR-0159 決定5）
    // ------------------------------------------------------------------

    private static OrderScreeningResult EvaluateWithBan(
        OrderIntent intent, ShortSellOrderContext? context, DateOnly? banUntil) =>
        RiskEvaluator.Evaluate(
            intent, Settings(), Snapshot(), null, context, null, new BuyInBanSupply(Today, banUntil));

    // T-10-230: FR-10, ADR-0016 決定4（2026-08-06 改訂）, IADR-0159 決定5 ——
    // **借株照会の供給元が無く空売り文脈が組めなくても**、推定台帳から供給された禁止期限だけで
    // `BuyInBanned` を記録できる。文脈が null のとき ShortSellEvaluator は BorrowUnavailable で
    // **打ち切る**ため、この経路が無いと「禁止期間中であること」が監査ログ（FR-11）に一切残らない。
    [Theory]
    [InlineData(0, true)]   // 推定当日
    [InlineData(29, true)]  // 29 日後（禁止中）
    [InlineData(30, false)] // 30 日後（解除）
    public void 空売り文脈が組めなくても禁止期限の単独供給で拒否理由を記録できる(int daysSince, bool expectedRejected)
    {
        var banUntil = Limits.BuyInBanUntil(Today.AddDays(-daysSince));

        var result = EvaluateWithBan(ShortEntry(1_000m), context: null, banUntil);

        result.IsApproved.Should().BeFalse("文脈が無い間はいずれにせよ借株不可で止まる（フェイルクローズ）");
        result.Reasons.Contains(RejectionReason.BuyInBanned).Should().Be(expectedRejected);
        result.Reasons.Should().Contain(RejectionReason.BorrowUnavailable);
    }

    // T-10-231（**否定形**）: 供給された禁止が**無い**のに BuyInBanned が立ってはならない。
    // 立てば、日報・月報の「強制買戻しの発生有無」（決定15）が起きていない事象で水増しされる。
    [Fact]
    public void 禁止期限が供給されていなければ強制買戻しの拒否理由は立たない()
    {
        EvaluateWithBan(ShortEntry(1_000m), context: null, banUntil: null)
            .Reasons.Should().NotContain(RejectionReason.BuyInBanned);
    }

    // T-10-232: 文脈経由（ShortSellEvaluator）と単独供給（BuyInBanSupply）の**両方**から立っても
    // 拒否理由は 1 件である（借株照会が実装された日に理由が二重計上されない）。
    [Fact]
    public void 文脈と単独供給の両方から禁止が立っても拒否理由は二重計上されない()
    {
        var banUntil = Limits.BuyInBanUntil(Today);

        var result = EvaluateWithBan(
            ShortEntry(1_000m), Context(shortPermit: true, buyInBanUntil: banUntil), banUntil);

        result.Reasons.Count(r => r == RejectionReason.BuyInBanned).Should().Be(1);
    }

    // T-10-233（**否定形**）: 単独供給の経路でも、`BuyInBanned` は**クラス A のまま**である。
    // クラス C（`BannedSymbol` / `ManipulativeOrderPattern` ＝段階昇格ゲートの「統制違反 0 件」の計上対象）へ
    // 混ぜると、市況由来の事象が「AI が禁止事項を犯そうとした件数」に混入し、ゲートが機能しなくなる。
    [Fact]
    public void 単独供給による強制買戻しの拒否もクラスCに計上されない()
    {
        var result = EvaluateWithBan(ShortEntry(1_000m), context: null, Limits.BuyInBanUntil(Today));

        result.Reasons.Should().Contain(RejectionReason.BuyInBanned);
        result.Reasons.Should().NotContain(RejectionReason.BannedSymbol);
        RejectionReasonClassification.CountsAsControlViolation(result.Reasons).Should().BeFalse();
    }

    // T-10-234: 手仕舞い（買い戻し）は禁止期間中でも止まらない（ADR-0009 の不変条件）。
    // 禁止は**新規建て**にのみ効く。止めれば損失に上限の無い建玉を閉じられなくなる。
    [Fact]
    public void 禁止期間中でも空売りの買い戻しは止まらない()
    {
        var buyToCover = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.ShortSell, BrokerProvider.InternalPaper,
            Quantity: 1_000, Price: 1.00m, PositionEffect.Close);

        var result = EvaluateWithBan(buyToCover, context: null, Limits.BuyInBanUntil(Today));

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().BeEmpty();
    }
}
