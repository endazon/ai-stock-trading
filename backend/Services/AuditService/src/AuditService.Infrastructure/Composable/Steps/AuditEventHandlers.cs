using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using Wolverine;

namespace AiStockTrading.Audit.Infrastructure.Composable.Steps;

// FR-11, UC-07, IADR-0019: 全ドメインイベントを購読して監査台帳へ記録するハンドラ群。
// 冪等キーはメッセージ ID（再送で重複記録しない）。記録時刻は IClock。写像は AuditEntryFactory（純関数）。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<T> から Wolverine のハンドラへ移行した。
// 冪等キーは `context.MessageId`（`Guid?`・null なら新規採番していた）から `envelope.Id`（`Guid`・非 null）
// になり、**「MessageId が無ければ重複排除できない」分岐が構造的に不要**になった（Wolverine は送信時に必ず採番する）。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする（Wolverine は public でない型を受け付けない）。
// **契約イベントの全数**それぞれに 1 本ずつキューを持つ（ai-stock-trading.audit-service.<イベント型名>）。
// #339: ここに件数を書かない —— 件数はイベントを 1 つ足すたびに腐る導出値であり、実測でも
// 「22」と書いたまま 33 まで乖離していた。**全数一致は AuditConsumerCoverageTests が機械で保証する。**

public sealed class PriceMovementDetectedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(PriceMovementDetected message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class StopLossTriggeredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(StopLossTriggered message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class TradeDecisionMadeAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(TradeDecisionMade message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class OrderApprovedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(OrderApproved message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class OrderRejectedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(OrderRejected message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class OrderExecutedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(OrderExecuted message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-05, FR-19, #154, IADR-0067: 注文の訂正（注文履歴テレメトリ）を監査台帳へ記録する（FR-11: 全イベントの時系列記録）。
public sealed class OrderModifiedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(OrderModified message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-05, FR-19, #154, IADR-0067: 注文の取消（注文履歴テレメトリ）を監査台帳へ記録する。
public sealed class OrderCancelledAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(OrderCancelled message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-17: 全体前提条件の変更（設定管理 #19）を監査台帳へ記録する。
public sealed class AssumptionsChangedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(AssumptionsChanged message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-07: 報告書の確定（報告書 #14）を監査台帳へ記録する。
public sealed class ReportConfirmedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(ReportConfirmed message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-06/07/09, FR-11, IADR-0116, #280: 報告書ドラフトの提示（自動生成スケジューラ）を監査台帳へ記録する。
// 確定（ReportConfirmed）と同じ相関で束ねられ、提示から確定までのリードタイムを監査照会で辿れる。
public sealed class ReportDraftPresentedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(ReportDraftPresented message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// NFR（費用）: 費用しきい値到達（費用統制 #23）を監査台帳へ記録する。
public sealed class CostThresholdReachedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(CostThresholdReached message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// NFR（費用）, FR-04, IADR-0055: 実 LLM 費用の発生（#79）も監査台帳へ記録する（FR-11: 全イベントの時系列記録）。
public sealed class LlmCostIncurredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(LlmCostIncurred message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-01, FR-02: 情報収集の完了（情報収集 #9）を監査台帳へ記録する。
public sealed class InformationCollectedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(InformationCollected message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-20, #167, IADR-0082: 段階ゲートの遷移（承認による昇格・差し戻し #20/#167）を中央監査台帳へ記録する。
public sealed class StageTransitionedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(StageTransitioned message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-19, FR-11, #464, ADR-0028 決定2, IADR-0182: GFV 違反による停止の解除を中央監査台帳へ記録する。
// **解けたのは停止であって記録ではない**（決定1「違反記録は失効させない」）。
public sealed class GoodFaithViolationsClearedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(GoodFaithViolationsCleared message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, #465, ADR-0027 決定1, IADR-0183: 借株料の**日次の計上額**を中央監査台帳へ記録する。
// **累計だけを保持すると、後から日別の内訳を復元できない**（決定1）。
public sealed class BorrowFeeAccruedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BorrowFeeAccrued message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, #465, ADR-0027 決定4, IADR-0183: 借株料を**計上できなかった日**を中央監査台帳へ記録する。
// 🔴 **0 円の計上として残さない**——「費用が発生しなかった」と「取得できなかった」は別の事実である。
public sealed class BorrowFeeAccrualUnavailableAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BorrowFeeAccrualUnavailable message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-20, #166, IADR-0083: 撤退基準到達（自動安全側の発火・撤退の定期評価ドライバ #166）を中央監査台帳へ記録する。
public sealed class WithdrawalTriggeredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(WithdrawalTriggered message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-20, FR-15, #164, IADR-0089: バックテスト verdict（Stage 0 合格判定 #16・Stage 0→1 解錠）を中央監査台帳へ記録する。
public sealed class BacktestEvaluatedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BacktestEvaluated message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, UC-06, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い要求を中央監査台帳へ記録する。
// 後続の OrderApproved はアクターも理由も持たないため、本記録が「誰が・なぜ落としたか」の唯一の証跡になる。
public sealed class PositionCloseRequestedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(PositionCloseRequested message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップ（取引判断 #11）を中央監査台帳へ記録する（全イベントの時系列記録・FR-11）。
public sealed class DailyPolicyUnconfirmedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(DailyPolicyUnconfirmed message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-05, FR-10, FR-11, #292, IADR-0118: ブローカ実ポジションの観測を中央監査台帳へ記録する（全イベントの時系列記録・FR-11）。
public sealed class BrokerPositionsObservedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BrokerPositionsObserved message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-20, FR-05, FR-11, #385, IADR-0150: ブローカ稼働の観測を中央監査台帳へ記録する（全イベントの時系列記録・FR-11）。
// **Stage 1 の営業日数の一次証跡である。** 「その日の稼働分数」の根拠は個々の観測の並びにしか無く、
// リスク管理側が持つのは集計後の分数だけである（§4.2 が「日次の稼働分数…を記録する」と求める監査可能性）。
public sealed class BrokerAvailabilityObservedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BrokerAvailabilityObserved message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-19, FR-10, FR-11, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測を中央監査台帳へ記録する。
// **口座種別は統制の適用可否を決める最上位の条件**であり、「いつ・どの口座種別が観測されたか」の並びが無いと、
// 事後に「なぜその時刻の注文で GFV 回避ガードが効かなかったのか」を再構成できない。
// 判定側（リスク管理）が持つのは最新 1 件だけである（履歴を持たない・IADR-0153 決定3）。
public sealed class BrokerAccountObservedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BrokerAccountObserved message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-05, FR-10, FR-11, #292, IADR-0118: 台帳とブローカの乖離検知を中央監査台帳へ記録する。
// 是正は伴わないため、この記録が「いつ・どの銘柄で乖離が生じたか」の唯一の永続証跡になる。
public sealed class PositionReconciliationDriftAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(PositionReconciliationDrift message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
// 強制買戻しの**推定**を中央監査台帳へ記録する。**これは検知ではなく推定であり、取り違えがあり得る**。
// だからこそ根拠（消失した建玉・突合した自らの決済約定・処理中の決済・推定日時）を残し、
// 人が後から「その推定は妥当だったか」を検証できるようにする（#419 の設計上の注意）。
public sealed class BuyInInferredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(BuyInInferred message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0165:
// **未決済資金による買付（GFV 発生）の自前計数**を中央監査台帳へ記録する。
//
// ★ 記録しているのは「**自らのガードをすり抜けた買付**」であり、**ブローカーが GFV と判定した件数ではない**
//   （ADR-0025 §理由。両者が一致する保証はない）。**ガードが正しく働けば 1 件も記録されない。**
//
// ADR-0025 が手入力を採らなかった理由の 1 つが「moomoo のアプリ表示を人が転記する経路は監査証跡（FR-11）に
// 乗らない」ことであった。本ハンドラがその要求に応える。
public sealed class GoodFaithViolationRecordedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(GoodFaithViolationRecorded message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, UC-06, #330, IADR-0133: 維持率割れによる建玉の自動縮小を中央監査台帳へ記録する。
// 利用者の承認も AI も介在しない自動決済であるため、**記録が無ければ「知らないうちに建玉が減っていた」状態**に
// なる（04_report-templates の記載理由）。日報・月報の記載もこの同じイベントから作る。
public sealed class MaintenanceMarginReductionExecutedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(MaintenanceMarginReductionExecuted message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の劣化を監査台帳へ記録する 3 本。
//
// 🔴 **監査台帳に残す意味は「後から期間を復元できる」ことである。** 通知は流れて消えるが、
// 台帳は残る——「いつからいつまで劣化した情報源で判断していたか」は、
// 事後に取引を検証するときに要る（FR-11 の 7 年保持）。
public sealed class FxRateSourceFellBackAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(FxRateSourceFellBack message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-06, FR-10, FR-11, #513, ADR-0022 決定1, IADR-0225: **どの情報源を使ったか**を暦日ごとに台帳へ残す。
//
// 🔴 **本ハンドラが無いと「静かな期間」の出典を後から証明できない。** 切替・復帰は遷移でしか出ないため、
// 平常時の台帳は空白であり、**「静かに第一の源を使った」と「為替を一度も使わなかった」の区別が付かない。**
public sealed class FxRateSourceUsedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(FxRateSourceUsed message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class FxRateSourcePrimaryRestoredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(FxRateSourcePrimaryRestored message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

public sealed class FxRateStaleAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(FxRateStale message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, #381, ADR-0022 決定5, IADR-0198: 鮮度切れのレートで決済した事実を台帳へ記録する。
//
// 🔴 **本ハンドラが「いつのレートで決済したか」を 7 年保持へ入れる唯一の経路である。**
// 台帳の行（ApprovedOrderRow）は FxRateToBase の数値だけを持ち観測日の列が無いが、
// 台帳は**イベント全量を JSON で保存する**ため、ここを通ることで RateAsOf が残る。
public sealed class PositionClosedWithStaleFxRateAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(PositionClosedWithStaleFxRate message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-11, UC-07, ADR-0016 決定15, #339, IADR-0226: 取引記録の経費 1 行（経費区分 7 種）。
//
// 🔴 **監査台帳が経費台帳の保存先である。** 専用の永続テーブルを作らず、イベント全量の JSON を
// 7 年保持（NFR-10）する監査台帳へ残す。建玉単位の照会は相関（`trade-expense:{Symbol}:{Market}`）で成立する。
public sealed class TradeExpenseRecordedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(TradeExpenseRecorded message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-05, FR-11, ADR-0002（SPOF）, #331, IADR-0211: 発注の見送りを中央監査台帳へ記録する。
// キューイングしない裁定のため、この記録が「なぜ発注されなかったか」の唯一の永続証跡である
// （事前拒否・証券会社拒否とは別 EventType＝別集計）。
public sealed class OrderDispatchForgoneAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(OrderDispatchForgone message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-01, FR-11, #336, ADR-0020 決定3: 情報源の欠測による縮退を台帳へ記録する。
public sealed class InformationSourceDegradedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(InformationSourceDegraded message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, UC-02, #331, IADR-0210: 保護逆指値の発注を中央監査台帳へ記録する
// （「建玉あり ⇒ 有効な逆指値あり」の一次証跡）。
public sealed class ProtectiveStopPlacedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(ProtectiveStopPlaced message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-01, FR-11, #336, ADR-0020 決定2-3: 欠測からの回復（発生時刻・継続時間・該当サイクル数）を台帳へ記録する。
//
// 🔴 **本ハンドラが日報・月報の集計経路である。** 種別 × 期間の照会（IADR-0199 決定2）で引ける形で
// 残さないと、「欠測の月次合計を月報に記録する」（ADR-0020 決定2-3）が成立しない。
public sealed class InformationSourceRecoveredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(InformationSourceRecovered message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-10, FR-11, UC-02, #331, IADR-0210: 保護逆指値が成立しなかったときの建玉解消を中央監査台帳へ記録する。
// 利用者の承認なしに注文取消・建玉決済が起きるため、記録が無いと「知らないうちに建玉が消えた」状態になる。
public sealed class ProtectiveStopCoverageLostAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(ProtectiveStopCoverageLost message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-04, FR-09, FR-11, ADR-0017 決定4-(3), #335, IADR-0217: フォールバック発火を台帳へ記録する。
//
// 🔴 **本ハンドラが「当月のフォールバック発火回数（用途別・原因別）」の唯一の供給元である。**
// ADR-0017 決定4-(3) は月報への集計掲載を求めるが、集計は記録が残っていて初めて可能になる。
public sealed class LlmFallbackFiredAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(LlmFallbackFired message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-01, FR-11, #336, ADR-0020 決定4: 一般インターネット収集の発動／解除を台帳へ記録する。
// ADR-0020 決定4 は「発動・解除はいずれも監査ログに残し、月報に記載する」ことを求めている。
public sealed class GeneralWebCollectionStateChangedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(GeneralWebCollectionStateChanged message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-04, FR-09, FR-11, UC-01, ADR-0017 決定2, #335, IADR-0216: 取引判断の見送りを台帳へ記録する。
//
// 🔴 **見送りは障害ではなく設計上の正常な結果である。** 記録する理由は障害追跡ではなく、
// ADR-0017 決定2 が「日報に当日のスキップ回数を記載する」と定めたためである
// （取引機会を逸した回数は、日報を方針書として読むうえで必要な情報である）。
public sealed class TradeDecisionSkippedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(TradeDecisionSkipped message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}

// FR-02, FR-04, FR-11, #337, IADR-0247: スクリーニング入力の縮退（分割/切り詰め）を台帳へ記録する。
// 月報の件数記載（分割と切り詰めを分けて数える）は台帳の種別 × 期間照会が集計経路である。
public sealed class ScreeningContextReducedAuditHandler(IAuditEventStore store, IClock clock)
{
    public void Handle(ScreeningContextReduced message, Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        store.Append(AuditEntryFactory.From(message, envelope.Id, clock.UtcNow));
    }
}
