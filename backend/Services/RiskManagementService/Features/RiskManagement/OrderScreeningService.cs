using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-19, FR-20, UC-01, UC-02, ADR-0003: 取引判断（TradeDecisionMade）を発注前に決定的に検証し、
// OrderApproved / OrderRejected を生成する。判定コア RiskEvaluator（ステートレス）に、ホストが保持する
// ロックアウト状態（IADR-0008）を合成する。日次損失上限の新規到達でロックアウトを設定し、含み損が回復しても
// 当日中（翌営業日の解除まで）は新規建てを止め続ける。手仕舞い（Close）はフェイルセーフで常に通す。
//
// FR-10, #428, IADR-0163 決定2: **推定台帳（buyInInferences）は必須依存である。**
// 省略可能引数（既定 `null`）で受けていると、`Program.cs` から引数を削っても**コンパイルが通りテストは
// 全緑のまま強制買戻し由来の 30 日禁止だけが静かに効かなくなる**（既存テストは本サービスを直接構築するため
// 配線の消失を検知しない）。**不在が統制の無効を意味する依存は必須にする**——同じ規律で
// `BrokerPositionsObservedHandler` は既に依存を必須にしている（IADR-0159）。
// **`patternDetector` は省略可能のままである**——「検出器を構成していない」は正当な状態であり、
// `null` の意味が違う（推定台帳の `null` は「30 日禁止が効かない」を意味する）。
public sealed class OrderScreeningService(
    IRiskSettingsStore settingsStore,
    PortfolioSnapshotBuilder snapshotBuilder,
    ILockoutStore lockoutStore,
    IClock clock,
    IBusinessCalendar businessCalendar,
    IBuyInInferenceStore buyInInferences,
    IManipulativeOrderPatternDetector? patternDetector = null)
{
    public ScreeningOutcome Screen(TradeDecisionMade decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var intent = decision.Intent;
        // #337（#249 吸収）, IADR-0246: 日次損失ロックアウトの「当日」は**注文の市場の現地取引日**で解釈する。
        // JST 固定（clock.Today）では ET 10-11 時（セッション中）に日付が変わり、同一の米国セッションの
        // 途中でデイリーストップが解除されていた。導出は TradingDay.Of（単一情報源）。
        var tradingDay = TradingDay.Of(clock.UtcNow, intent.Market);
        var settings = settingsStore.GetCurrent();
        var snapshot = snapshotBuilder.Build();
        var isEntry = intent.PositionEffect == PositionEffect.Open;

        // FR-10, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 決定5:
        // 強制買戻し由来の 30 日禁止を判定コアへ供給する。**借株照会の供給元が無いため空売り文脈
        // （ShortSellOrderContext）は今も組めない**が、禁止期限だけは推定台帳から供給できる。
        // 供給できない値（維持率・エクスポージャ）を 0 で埋めた偽の文脈は作らない（値を発明しない）。
        // #428, IADR-0163 決定2: 台帳は必須依存であり、供給は**常に**組む（禁止が無ければ BanUntil が null）。
        var buyInBan = new BuyInBanSupply(clock.Today, buyInInferences.GetBanUntil(intent.Symbol, intent.Market));

        // 判定コア（決定的）を実行し、違反理由を集約する。
        var result = RiskEvaluator.Evaluate(intent, settings, snapshot, patternDetector, buyInBan: buyInBan);
        var reasons = new List<RejectionReason>(result.Reasons);

        // 日次損失上限に「新規到達」したら当日ロックアウトを設定する（翌営業日まで）。
        // RiskEvaluator は実現+含み損の合算で当該時点の到達を判定する（IADR-0008）。
        if (reasons.Contains(RejectionReason.DailyLossLimitReached))
        {
            EngageLockout(tradingDay);
        }
        else if (isEntry && IsLockedOut(tradingDay))
        {
            // ロックアウトは当日中維持する。含み損が回復して RiskEvaluator が到達と判定しなくても、
            // 一度到達した当日は翌営業日の解除まで新規建てを拒否し続ける（デイリーストップの趣旨）。
            // 手仕舞い（Close）は isEntry の短絡で本分岐に入らず、フェイルセーフで常に通す。失効した
            // ロックアウトの掃除（IsLockedOut 内の Clear）は次の新規建て評価時に走れば十分で、Close の
            // 可否には影響しないため、この短絡は意図どおり（掃除が遅れても状態は失効判定で無効化される）。
            reasons.Add(RejectionReason.DailyLossLimitReached);
        }

        // FR-20, FR-11, #387, IADR-0148 決定3: 段階ゲートの「統制違反 0 件」（クラス C 限定）を数えるための観測。
        // **承認でも拒否でも作る**——算入対象の発注先で審査が動いていること自体が「集計が供給されている」根拠であり、
        // 拒否だけを観測すると「違反 0 件」と「そもそも数えていない」を区別できない（#387 の fail-open）。
        // 発注先は**その注文が向いていた先**（intent.Mode）を用いる。クラス分けはここで行わない
        // （単一情報源は RejectionReasonClassification・集計は ControlViolationAggregation）。
        var observation = new OrderScreeningObservation(decision.DecisionId, intent.Mode, reasons);

        if (reasons.Count > 0)
        {
            return ScreeningOutcome.Reject(
                new OrderRejected(decision.DecisionId, intent, reasons, clock.UtcNow), observation);
        }

        // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点を判断から発注執行へ**そのまま**中継する
        // （統制の判定には一切使わない・審査時刻で上書きしない）。上書きすると審査より前の区間が消える。
        return ScreeningOutcome.Approve(
            new OrderApproved(
                decision.DecisionId, intent, result.ApprovedQuantity, clock.UtcNow,
                decision.CycleTrigger, decision.CycleStartedAt),
            observation);
    }

    // #249 / IADR-0246: 当日（tradingDay）は呼び出し側が注文の市場の現地取引日で解決して渡す。
    private bool IsLockedOut(DateOnly tradingDay)
    {
        var lockout = lockoutStore.Get();
        if (lockout is null)
        {
            return false;
        }

        // 翌営業日の解除日に達していれば失効させ、状態を掃除する。
        if (!lockout.IsActiveOn(tradingDay))
        {
            lockoutStore.Clear();
            return false;
        }

        return true;
    }

    private void EngageLockout(DateOnly tradingDay)
    {
        var existing = lockoutStore.Get();
        // 既に当日有効なロックアウトがあれば解除日を延長しない（同日中の重複到達で解除が先送りされるのを防ぐ）。
        if (existing is not null && existing.IsActiveOn(tradingDay))
        {
            return;
        }

        var releaseOn = businessCalendar.NextBusinessDay(tradingDay);
        lockoutStore.Set(new LockoutState(
            releaseOn,
            "日次損失上限到達により当日ロックアウト（翌営業日まで）",
            clock.UtcNow));
    }
}
