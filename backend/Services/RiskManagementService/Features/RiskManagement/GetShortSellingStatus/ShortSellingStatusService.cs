using RiskManagementService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetShortSellingStatus;

// FR-10, UC-06, SC-03, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// SC-03「維持率・空売りの現況」の集約（表示専用・副作用なし）。
//
// **本サービスは値を作らない。** 供給元が無い指標は `MetricAvailability.NotSupplied` として返し、
// 画面がそれを「取得できていません」と描く。0 や空列へ倒すと、画面は正常な統制として描いてしまう。
//
// 供給の実測（2026-08-13 時点）:
//   維持率        … IMaintenanceMarginSnapshotSource の既定は UnavailableMaintenanceMarginSnapshotSource（常に null）
//   借株料の累計  … 記録側は入った（#465 / IADR-0183）が、**供給は ADR-0026 PoC 項目 9〔単位確定〕待ちで意図的に未開始**
//   自動縮小の履歴… 発火元（維持率）が無く、履歴ストアも照会 API も無い
//   空売り比率    … 建玉の射影はあるが、分母（建玉総額＝時価）に現在値が要る（MarketData:EnableMarkToMarket 既定 false）
//   建玉の方向    … **有る**（台帳射影 OpenPosition.Side）
//   強制買戻し回数… **結線済み**（#470 / IADR-0186）。当月が観測の届いた取引日で覆われていれば供給する
//                   （観測そのものは OpenD 常駐待ちのため、現況では未供給へ倒れる）
public sealed class ShortSellingStatusService(
    IRiskSettingsStore settingsStore,
    IPortfolioLedgerStore ledger,
    IMaintenanceMarginSnapshotSource snapshotSource,
    // FR-21, SC-03, #470, IADR-0186 決定4: **必須依存である**（省略可能引数にしない）。
    // 省略可能にすると配線を落としても既定値で緑のまま通る（IADR-0163 決定2 と同じ規律）。
    IPositionObservationArrivalStore observationArrivals,
    IBuyInInferenceStore buyInInferences,
    IClock clock,
    IBusinessCalendar calendar,
    ICurrentPriceSource? currentPrices = null)
{
    public ShortSellingStatusView Build()
    {
        var limits = settingsStore.GetCurrent().ShortSell.Limits;
        var snapshot = snapshotSource.GetCurrent();
        var positions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());

        // 現在値は建玉の評価額（＝空売り比率の分子・分母）に要る。供給が無ければ辞書は空になる。
        var prices = currentPrices?.GetCurrentPrices(positions)
            ?? new Dictionary<(string Symbol, Market Market), decimal>();

        var positionViews = positions
            .Select(p => BuildPosition(p, prices))
            .OrderBy(p => p.Symbol, StringComparer.Ordinal)
            .ToList();

        var (maintenanceAvailability, maintenanceRatio, appliedThreshold, appliedRecoveryTarget) =
            BuildMaintenanceMargin(snapshot, limits);

        var (exposureAvailability, exposureRatio) = BuildShortExposure(positionViews);

        var (buyInAvailability, buyInCount) = BuildBuyInCount();

        return new ShortSellingStatusView(
            MaintenanceMarginAvailability: maintenanceAvailability,
            MaintenanceMarginRatio: maintenanceRatio,
            AppliedMaintenanceMarginThreshold: appliedThreshold,
            AppliedMaintenanceRecoveryTarget: appliedRecoveryTarget,
            ConfiguredMaintenanceMarginThreshold: limits.MaintenanceMarginThreshold,
            MaintenanceRecoveryTargetOffset: limits.MaintenanceRecoveryTargetOffset,
            ShortExposureAvailability: exposureAvailability,
            ShortExposureRatio: exposureRatio,
            ShortExposureRatioCap: limits.ExposureRatioCap,
            Positions: positionViews,
            // 借株料の累計は**供給元がコードに存在しない**。0 を返すと「費用が発生していない」と読める。
            BorrowFeeAvailability: MetricAvailability.NotSupplied,
            TotalAccruedBorrowFeeUsd: null,
            // 発動履歴は**記録する経路がコードに存在しない**（履歴ストアも照会 API も無い）。空列＝「発動なし」と
            // 区別する（区別しないと「統制が働いていて発動が無かった」と読めてしまう。IADR-0133 決定8 と同じ規律）。
            //
            // **供給可否を `snapshot` に結び付けてはならない。** `snapshot` は維持率の供給有無であって、
            // 発動が記録されるか否かとは別の事実である。結び付けると、維持率の供給が入った瞬間
            // （#330 / #331 が実装された日）に本項が `NotApplicable`＝「概念が成立しない・正常」へ化け、
            // **記録経路が無いままの空列が「統制が働いていて発動が無かった」と読める**。
            // 供給が入った日は誰も画面を見直さないため、最も気づかれにくい向きの fail-open である。
            // **記録経路を実装するまでは無条件に未供給である。**
            ReductionHistoryAvailability: MetricAvailability.NotSupplied,
            ReductionHistory: [],
            // ADR-0016 決定15, FR-21, #470, IADR-0186: **強制買戻しの発生回数。**
            //
            // 推定台帳（`buy_in_inferences`）は**推定が起きたときにしか行を書かない**ため、
            // **行数 0 だけでは 2 つの別事実を区別できない**。
            //   1. ブローカ建玉の観測が一度も届いていない（＝**この統制はまったく働いていない**）
            //   2. 観測した結果、強制買戻しは 1 件も無かった（＝正常）
            //
            // **観測の到達（FR-21）で区別する**——#463 / IADR-0181 が記録経路を入れたため、
            // 本サービスは残っていた**結線**を行う（従前のコメント「記録経路が要る」は stale であった）。
            BuyInCountAvailability: buyInAvailability,
            BuyInCount: buyInCount);
    }

    // FR-21, FR-10, SC-03, UC-06, ADR-0016 決定15, #470, IADR-0186:
    // **強制買戻しの発生回数を「正当な 0」として供給できるかを判定する。**
    //
    // 計画 FR-21（2026-08-08 改定）:
    // > **報告期間が観測の届いた取引日で覆われている場合に限り、件数 0 を正当な 0 として供給する。**
    //
    // 🔴 **期間は当月（月初〜当日）である**（IADR-0186 決定1）。ADR-0016 決定15 は
    // **「発生回数」を月報（当月）に、「発生有無」を日報（当日）に**割り当てており、SC-03 の当該項目の
    // 名称は**発生回数**である。**計画は SC-03 の期間を明示していない**ため、決定15 の語彙から導出した
    // 最も説明可能な読みを採り、同時に計画へ環流している（発明ではなく導出であることを残す）。
    //
    // **判定規則を再実装しない。** 照会 API（#469）と同じ純関数 `ObservationCoverage.Covers` を通す
    // ——規則が 2 か所に分かれると必ず食い違う。**1 日でも欠ければ未供給**へ倒れる。
    //
    // **他の指標の供給状態を条件に混ぜない**（IADR-0154 残余リスク4）。維持率・現在値・空売り比率の
    // 供給が入った日に、本項の可否が黙って変わってはならない——入力は観測の到達と推定台帳だけである。
    private (MetricAvailability Availability, int? Count) BuildBuyInCount()
    {
        var today = clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var observedDays = observationArrivals.GetObservedDaysBetween(monthStart, today);

        // 覆えていない＝「観測が届いていない期間がある」。**0 件と描いてはならない**
        //（計画が名指しで禁じた向き。ADR-0016 決定15 / IADR-0162 決定2）。
        if (!ObservationCoverage.Covers(observedDays, monthStart, today, calendar))
        {
            return (MetricAvailability.NotSupplied, null);
        }

        // 覆えている＝観測した結果である。**0 件でも `Available` として返す**——
        // 正当な 0 を未供給に見せることも FR-21 の規約違反である（規約は両方向に効く）。
        return (MetricAvailability.Available, buyInInferences.CountInferredBetween(monthStart, today));
    }

    // ADR-0016 決定7（2026-08-07 追記）, IADR-0133 決定2: 維持率と、適用される閾値・回復目標。
    //
    // **維持率は Stage 1 の全期間にわたって供給されない**——実弾口座のヘッダを要し、moomoo SIMULATE では
    // 照会 API 自体が失敗する（ADR-0019 PoC 項目 3 の実測）。**これは直すべきバグではない。**
    // 本メソッドが常に NotSupplied を返すことは、供給が無いという事実の正しい表現である
    // （供給元が入れば同じコードのまま Available へ切り替わる。画面へ「未供給」と書き込まないのはこのため）。
    // 適用閾値は**建玉の株価に依存する**（自前 40% と規制要求の厳しい方を建玉ごとに出し、最も厳しいものを採る）。
    // スナップショットが無ければ株価が分からないため、閾値も回復目標も決められない（null）。
    private static (MetricAvailability Availability, decimal? Ratio, decimal? Threshold, decimal? RecoveryTarget)
        BuildMaintenanceMargin(Domain.MaintenanceMarginSnapshot? snapshot, Domain.ShortSellingLimits limits)
    {
        // 供給元が無い＝統制が働いていない。**運用上の異常**として NotSupplied で宣言する。
        if (snapshot is null)
            return (MetricAvailability.NotSupplied, null, null, null);

        // IADR-0133 決定8: 成立しない値が 1 件でもあればスナップショット全体を信頼しない。
        // 壊れた建玉だけを除くと分母が縮んで維持率が実際より良く見える（過少縮小へ倒れる）。
        // 画面でも同じ向きに倒す——「良く見える維持率」を出さない。
        if (!snapshot.IsTrustworthy)
            return (MetricAvailability.NotSupplied, null, null, null);

        // 建玉が 1 件も無ければ維持率という概念が成立しない（異常ではない）。
        if (snapshot.MaintenanceMarginRatio is not { } ratio)
            return (MetricAvailability.NotApplicable, null, null, null);

        var threshold = snapshot.Positions.Max(p => limits.MaintenanceMarginThresholdFor(p.PriceUsd));
        var recoveryTarget = snapshot.Positions.Max(p => limits.MaintenanceRecoveryTargetFor(p.PriceUsd));
        return (MetricAvailability.Available, ratio, threshold, recoveryTarget);
    }

    // ADR-0016 決定9: 空売り比率 ＝ 空売り建玉の合計 ÷ 建玉総額。**いずれも時価**である。
    // 取得原価で代用すると「空売り比率」という名前の別物になるため、評価額が 1 件でも欠ければ算出しない。
    private static (MetricAvailability Availability, decimal? Ratio) BuildShortExposure(
        IReadOnlyList<ShortSellingPositionView> positions)
    {
        if (positions.Count == 0)
            return (MetricAvailability.NotApplicable, null);

        if (positions.Any(p => p.MarketValueUsd is null))
            return (MetricAvailability.NotSupplied, null);

        var total = positions.Sum(p => p.MarketValueUsd!.Value);
        if (total <= 0m)
            return (MetricAvailability.NotApplicable, null);

        var shortValue = positions.Where(p => p.Side == TradeSide.Sell).Sum(p => p.MarketValueUsd!.Value);
        return (MetricAvailability.Available, shortValue / total);
    }

    // IADR-0107 決定4: 現在値・平均取得単価はローカル通貨。建玉の加重平均約定時レートで USD へ換算する
    // （含み損益の換算〔PortfolioValuation〕と同じ導出を使う＝評価額の定義を 2 つ作らない）。
    private static ShortSellingPositionView BuildPosition(
        OpenPosition position,
        IReadOnlyDictionary<(string Symbol, Market Market), decimal> prices)
    {
        var hasPrice = prices.TryGetValue((position.Symbol, position.Market), out var price);
        return new ShortSellingPositionView(
            Symbol: position.Symbol,
            Market: position.Market,
            Side: position.Side,
            Quantity: position.Quantity,
            AverageEntryPrice: position.AverageEntryPrice,
            MarketValueAvailability: hasPrice ? MetricAvailability.Available : MetricAvailability.NotSupplied,
            MarketValueUsd: hasPrice ? position.Quantity * price * position.FxRateToBase : null,
            // 借株料の累計は建玉単位でも供給元が無い。0 を入れると「費用が発生していない」と読める。
            BorrowFeeAvailability: MetricAvailability.NotSupplied,
            AccruedBorrowFeeUsd: null);
    }
}
