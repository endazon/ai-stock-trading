using AiStockTrading.Shared.Contracts.Events;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// FR-05, FR-10, #331, IADR-0210/0211: 1 件の OrderApproved 処理の結果。
// 発注した（Executed）か見送った（Forgone）かは排他であり、Open のエントリー発注には
// 保護逆指値の結果（StopPlaced または CoverageLost）が付随し得る。
// 発行（Publish）は Worker 層（OrderApprovedHandler）が非 null のイベントに対して行う。
// FR-10, ADR-0040 決定1, #819, IADR-0342 決定6: S2 では StopPlaced / CoverageLost の代わりに StopWaived が付く
// （3 つは排他。末尾の任意項目として足し、既存の生成箇所を変えない）。
// FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定3: S1 では SoftwareStopArmed が付く（上の 3 つと排他）。
// FR-10, ADR-0040 決定1（S3）, #821, IADR-0347: S3 では StopAttempted（試行の記録＝注文種別と拒否理由）が
// **StopPlaced または CoverageLost と一緒に**付く（排他ではない）——結果の扱いは S0 と同じであり、
// 試行の記録はその手前の事実だからである。
// 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355 決定5: 決済をブローカーの実建玉と突き合わせて乖離を見つけたら、
// **既存の乖離検知（IADR-0118）と同じイベント**（PositionReconciliationDrift）を添える。新しい通知経路を作らない
// ——監査台帳と Critical 通知の受け口は既にあり、二本目を作ると人が見る場所が割れる。
// 見送り（Forgone）にも発注（Executed・数量を縮めた場合）にも付き得るため、**排他にしない**（末尾の任意項目）。
// 🔴 #873 の監査（3 巡目）3: 乖離イベントが運ぶ数量は「台帳の決済数量」と「ブローカーのネット」であり、
// **この決済で実際に送った株数はどちらでもない**（両建てでは 3 つ目の数になる）。通知を読む人が
// 「送ったのか・何株送ったのか」を取り違えないよう、**送った株数を結果に添えてログへ出す**
// （イベントの形は変えない＝監査台帳・通知の受け口は不変）。見送りなら 0 である。
public sealed record OrderDispatchResult(
    OrderExecuted? Executed,
    OrderDispatchForgone? Forgone,
    ProtectiveStopPlaced? StopPlaced,
    ProtectiveStopCoverageLost? CoverageLost,
    ProtectiveStopWaived? StopWaived = null,
    AlternativeProtectiveStopAttempted? StopAttempted = null,
    PositionReconciliationDrift? Drift = null,
    int DriftDispatchedQuantity = 0,
    SoftwareStopArmed? SoftwareStopArmed = null)
{
    public static OrderDispatchResult FromExecuted(
        OrderExecuted executed,
        ProtectiveStopPlaced? stopPlaced = null,
        ProtectiveStopCoverageLost? coverageLost = null,
        ProtectiveStopWaived? stopWaived = null,
        AlternativeProtectiveStopAttempted? stopAttempted = null,
        PositionReconciliationDrift? drift = null,
        int driftDispatchedQuantity = 0,
        SoftwareStopArmed? softwareStopArmed = null) =>
        new(executed, null, stopPlaced, coverageLost, stopWaived, stopAttempted, drift, driftDispatchedQuantity,
            softwareStopArmed);

    // 見送りは 1 株も送っていない（DriftDispatchedQuantity は 0 のまま）。
    public static OrderDispatchResult FromForgone(
        OrderDispatchForgone forgone, PositionReconciliationDrift? drift = null) =>
        new(null, forgone, null, null, null, null, drift);
}
