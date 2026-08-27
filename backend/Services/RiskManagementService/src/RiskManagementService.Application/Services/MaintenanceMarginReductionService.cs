using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-11, UC-06, ADR-0003, ADR-0009, ADR-0016 決定7, #330, IADR-0133: 維持率割れによる建玉の自動縮小。
//
// **「動かす」統制**である——3 統制（kill switch / 日次損失ロックアウト / 一時停止）が「新規建てを止める」のに対し、
// 本統制は**システムが自ら決済注文を発注する**（UC-06 の 2 統制比較表）。したがって:
//
//   - **利用者の承認を待たない**（マージンコールは口座を失う唯一の経路であり、米国市場の開場は日本時間の深夜）
//   - **AI を介在させない**（判定は純関数 MaintenanceMarginReducer。LLM 関連の型を依存に持たない）
//   - **3 統制が成立していても動く**（統制ストア〔IKillSwitchStore/ILockoutStore/IPauseStore〕を依存に持たない
//     ことが構造的な保証である。本統制自体が手仕舞いであり、統制で止めることは ADR-0009 違反になる）
//   - **発注前スクリーニング（RiskEvaluator）を通さない**（同上。PositionCloseService と同じ規律）
//
// 供給元（維持率・純資産・必要証拠金）と定期評価のドライバは未実装であり #342 / #331 が担当する。
public sealed class MaintenanceMarginReductionService(
    IRiskSettingsStore settingsStore,
    IMaintenanceMarginSnapshotSource snapshotSource,
    IClock clock,
    IPortfolioLedgerStore? ledger = null,
    ILogger<MaintenanceMarginReductionService>? logger = null)
{
    /// <summary>
    /// 現在の維持率を評価し、発動条件（維持率 ≦ 閾値）を満たすなら縮小の決済注文と記録イベントを組み立てる。
    /// <para>
    /// 戻り値は 3 状態を**区別する**（IADR-0133 決定8）。とくに <see cref="MaintenanceMarginEvaluationStatus.SnapshotUntrusted"/>
    /// を <see cref="MaintenanceMarginEvaluationStatus.NoActionRequired"/> と同じ「何も起きなかった」に
    /// 潰さないこと——**統制が実質オフラインになっているのに誰も気づかない**状態を作らないためである。
    /// </para>
    /// </summary>
    public MaintenanceMarginEvaluation Evaluate()
    {
        var limits = settingsStore.GetCurrent().ShortSell.Limits;
        var snapshot = snapshotSource.GetCurrent();

        // IADR-0133 決定8: 値として成立しない建玉（株価・数量が 0 以下／必要証拠金が負）が 1 件でもあれば、
        // スナップショット全体を信頼せず決済しない。壊れた建玉だけを除くと分母が縮んで維持率が実際より
        // 良く見え、過少縮小へ倒れる。**拒否したことは必ず観測可能にする**（黙って何もしない状態を作らない）。
        if (snapshot is { IsTrustworthy: false })
        {
            var untrusted = snapshot.UntrustedPositions;
            logger?.LogWarning(
                "維持率スナップショットに成立しない値（株価・数量が 0 以下／必要証拠金が負）が {Count} 件含まれるため、"
                + "自動縮小の評価を見送りました（統制が働いていません。銘柄: {Symbols}）。"
                + "供給元のフィードを確認してください。",
                untrusted.Count,
                string.Join(", ", untrusted.Select(p => $"{p.Symbol}/{p.Market}")));

            return MaintenanceMarginEvaluation.Untrusted(untrusted);
        }

        if (MaintenanceMarginReducer.Plan(snapshot, limits) is not { } plan)
            return MaintenanceMarginEvaluation.NoAction;

        var executedAt = clock.UtcNow;
        var approvals = new List<OrderApproved>(plan.Legs.Count);
        var items = new List<MaintenanceMarginReductionItem>(plan.Legs.Count);

        // #257, IADR-0107: 決済レグへ建玉の加重平均約定時レートを引き継ぐ。引き継がないと外貨建て建玉の
        // 決済だけが未換算（レート 1）で台帳へ積まれ、基準通貨の実現損益が桁で誤る。本統制が閉じるのは
        // 信用建玉・空売り建玉＝**米国株（USD 建て）**であり、まさにその経路である。
        var fxRates = ResolveFxRates();

        foreach (var leg in plan.Legs)
        {
            // 決済は建玉方向の反対売買。ロング（信用買い）は Sell、ショート（空売り）は Buy で手仕舞う。
            var closeSide = leg.PositionSide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;

            var intent = new OrderIntent(
                leg.Symbol,
                leg.Market,
                closeSide,
                leg.ProductType,
                settingsStore.GetCurrent().Stage.Mode,
                leg.Quantity,
                leg.PriceUsd,
                PositionEffect.Close,
                // 決済注文に損切りは無い（建玉側の損切りは射影が保持する）。
                StopLossPrice: null,
                FxRateToBase: fxRates.GetValueOrDefault((leg.Symbol, leg.Market), 1m));

            // 各縮小は独立した決済である。再送の重複排除は発注執行側の DecisionId 予約が担う
            // （PositionCloseService と同じ。損切りのような決定的 ID は持たせない）。
            approvals.Add(new OrderApproved(Guid.NewGuid(), intent, leg.Quantity, executedAt));

            items.Add(new MaintenanceMarginReductionItem(
                leg.Symbol, leg.Market, leg.PositionSide, leg.ProductType,
                leg.Quantity, leg.PriceUsd, leg.RequiredMarginUsd));
        }

        // IADR-0133 決定7: 記録先 4 つ（監査・Discord・日報・月報）はこのイベントだけを写像する。
        var executed = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(),
            plan.RatioBefore,
            plan.Threshold,
            plan.RecoveryTarget,
            plan.RatioAfter,
            items,
            executedAt);

        return MaintenanceMarginEvaluation.Reduce(
            new MaintenanceMarginReductionOutcome(plan, approvals, executed));
    }

    // 台帳の建玉が持つ加重平均約定時レート（IADR-0107 決定4 と同じ近似＝外部 FX 源に依存しない）。
    // 台帳未注入・該当建玉なしはレート 1（基準通貨建てとみなす）。**縮小は止めない**——
    // 換算レートが引けないことを理由に手仕舞いを見送るのは ADR-0009 に反する（旧・損切り機械執行〔IADR-0015〕と同じ判断）。
    private Dictionary<(string Symbol, Market Market), decimal> ResolveFxRates() =>
        ledger is null
            ? []
            : PortfolioProjection.ProjectOpenPositions(ledger.GetFills())
                .ToDictionary(p => (p.Symbol, p.Market), p => p.FxRateToBase);
}

// 自動縮小 1 回分の出力。決済注文（発注執行へ渡す）と記録イベント（4 記録先へ渡す）を対で返す。
// 片方だけを発行できる形にしない——決済したのに記録が残らない状態が
// 「知らないうちに建玉が減っていた」（04_report-templates）そのものであるため。
public sealed record MaintenanceMarginReductionOutcome(
    MaintenanceMarginReductionPlan Plan,
    IReadOnlyList<OrderApproved> Approvals,
    MaintenanceMarginReductionExecuted Executed);

// FR-10, UC-06, #330, IADR-0133 決定8: 1 回の評価の結果。
// **「何もしなかった」を 1 つに潰さない**——健全ゆえの無動作（NoActionRequired）と、
// 入力が壊れていて評価できなかった（SnapshotUntrusted）は、運用上まったく別の事実である。
public enum MaintenanceMarginEvaluationStatus
{
    /// <summary>発動条件を満たさない（供給なし・建玉なし・維持率が閾値を上回る）。統制は正常に働いている。</summary>
    NoActionRequired,

    /// <summary>発動した（決済注文と記録イベントを組み立てた）。</summary>
    Reduced,

    /// <summary>
    /// スナップショットに成立しない値が含まれ、**評価そのものを見送った**（＝統制が働いていない）。
    /// 呼び出し側（定期評価ドライバ・#331）は、これを無動作と同じに扱わず利用者へ届けること。
    /// </summary>
    SnapshotUntrusted,
}

public sealed record MaintenanceMarginEvaluation(
    MaintenanceMarginEvaluationStatus Status,
    MaintenanceMarginReductionOutcome? Outcome = null,
    IReadOnlyList<MarginPosition>? UntrustedPositions = null)
{
    public static MaintenanceMarginEvaluation NoAction { get; } = new(MaintenanceMarginEvaluationStatus.NoActionRequired);

    public static MaintenanceMarginEvaluation Reduce(MaintenanceMarginReductionOutcome outcome) =>
        new(MaintenanceMarginEvaluationStatus.Reduced, outcome);

    public static MaintenanceMarginEvaluation Untrusted(IReadOnlyList<MarginPosition> positions) =>
        new(MaintenanceMarginEvaluationStatus.SnapshotUntrusted, UntrustedPositions: positions);
}
