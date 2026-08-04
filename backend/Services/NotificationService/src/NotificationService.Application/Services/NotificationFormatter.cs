using System.Globalization;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Notification.Application.Services;

// FR-09, UC-01, UC-02, UC-06: ドメインイベントを種別ごとのテンプレートで NotificationMessage に整形する純関数群。
public static class NotificationFormatter
{
    // 取引実行（約定）。全量約定は Info、それ以外（拒否・取消等）は注意喚起の Warning。
    public static NotificationMessage From(OrderExecuted e) => new(
        "取引実行",
        $"約定 {e.Status} 数量{e.FilledQuantity}@{e.AveragePrice}（OrderId={e.OrderId}・DecisionId={e.DecisionId}）",
        e.Status == OrderStatus.Filled ? NotificationSeverity.Info : NotificationSeverity.Warning);

    // リスク統制発動: 発注拒否（理由つき）。
    public static NotificationMessage From(OrderRejected e) => new(
        "リスク統制: 発注拒否",
        $"{e.Intent.Symbol} 拒否: {string.Join(",", e.Reasons)}（DecisionId={e.DecisionId}）",
        NotificationSeverity.Warning);

    // リスク統制発動: 損切りライン到達（機械執行の起点）。
    public static NotificationMessage From(StopLossTriggered e) => new(
        "リスク統制: 損切りライン到達",
        $"{e.Symbol} 損切り SL={e.StopLossPrice}（現在 {e.Price}・数量 {e.Quantity}・建玉 {e.PositionSide}）",
        NotificationSeverity.Critical);

    // FR-17: 全体前提条件の変更（利用者による設定変更の通知）。
    public static NotificationMessage From(AssumptionsChanged e) => new(
        "設定変更: 全体前提条件",
        $"前提条件が更新されました（v{e.Version}・{e.Actor}）: {e.Reason}",
        NotificationSeverity.Info);

    // FR-07, FR-09: 報告書の確定（方針が取引に有効化された通知）。
    public static NotificationMessage From(ReportConfirmed e) => new(
        "報告書確定",
        $"{e.Kind} 報告書 {e.PeriodKey} が確定しました（{e.Actor}・前提条件 v{e.AssumptionsVersion}）。",
        NotificationSeverity.Info);

    // FR-06/07/09, UC-03〜05, IADR-0116, #280: 報告書ドラフトの提示（＝確定依頼）。
    // 要約は発行側でサニタイズ済み（IADR-0116 決定3/4）。確定は利用者のみが行う（ADR-0003）ため本文で確定を促し、
    // 版番号を載せる（確定 API は版番号付き冪等・IADR-0024。通知だけで期待版が分かるようにする）。
    public static NotificationMessage From(ReportDraftPresented e) => new(
        "報告書ドラフト（承認待ち）",
        $"{e.Summary}\n\n"
            + $"内容を確認のうえ確定してください（{e.PeriodKey}・版 {e.Version}）。"
            + "確定するまで取引方針は変わりません。",
        NotificationSeverity.Info);

    // NFR（費用）, FR-09: 費用しきい値到達（間隔延長/停止）。停止（Halted）は Critical、間隔延長（Throttled）は Warning。
    public static NotificationMessage From(CostThresholdReached e) => new(
        $"費用統制: {e.State}",
        $"{e.Category} 費用が月次上限の {e.Percent:F0}% に到達しました（{e.Month}・{e.State}）。",
        e.State == "Halted" ? NotificationSeverity.Critical : NotificationSeverity.Warning);

    // UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップ。確定を促す注意喚起（Warning）。
    // 日報が未確定の間は取引が見送られ続けるため、利用者に確定を促す（同一営業日内は 1 回に抑止済み・IADR-0096）。
    public static NotificationMessage From(DailyPolicyUnconfirmed e) => new(
        "取引スキップ: 日報未確定",
        $"確定済みの日報がないため取引を見送りました（営業日 {e.BusinessDay:yyyy-MM-dd}）。日報を確定してください。",
        NotificationSeverity.Warning);

    // FR-20, FR-09, UC-06, #166: 撤退基準到達（自動安全側の発火）。新規建ての自動停止を伴う撤退は Critical。
    // 段階の実降格は提案に留まる（確定は利用者承認による差し戻しを要する）ことを本文で明示する。
    public static NotificationMessage From(WithdrawalTriggered e) => new(
        "リスク統制: 撤退基準到達",
        $"撤退基準に到達しました（{e.Reason}）。"
            + $"{(e.HaltNewEntries ? "新規建てを自動停止しました。" : string.Empty)}"
            + $"Stage {e.ProposedStage} への差し戻しを提案します（確定は利用者承認が必要）。",
        e.HaltNewEntries ? NotificationSeverity.Critical : NotificationSeverity.Warning);

    // FR-05, FR-09, FR-10, #292, IADR-0118: 取引台帳とブローカ実ポジションの乖離。
    // 是正は行わない（自動で建玉を合わせにいかない）ため、利用者が判断できるよう双方の数量を並べて示す。
    // 台帳が誤っていれば統制上限の判定そのものが狂うため Critical とする。
    public static NotificationMessage From(PositionReconciliationDrift e) => new(
        "リスク統制: 建玉の乖離を検知",
        $"取引台帳とブローカの建玉が一致しません（{e.Drifts.Count} 件・観測 {e.ObservedAt:yyyy-MM-dd HH:mm:ss}Z）。"
            + $"{string.Join("、", e.Drifts.Select(Describe))}。"
            + "自動是正は行いません。内容を確認し、必要なら決済または証券会社側で調整してください。",
        NotificationSeverity.Critical);

    // FR-09, FR-10, UC-06, #330, IADR-0133: 維持率割れによる建玉の自動縮小。
    // 利用者の承認を待たずシステムが決済したため **Critical**。本文には計画が日報へ求めた項目
    //（決済前後の維持率・閾値・回復目標・決済した建玉）をそのまま出す——「知らないうちに建玉が減っていた」
    // 状態を防ぐことが記録・通知の目的であり、数値が無ければ規則どおりの作動を利用者が確かめられない。
    public static NotificationMessage From(MaintenanceMarginReductionExecuted e) => new(
        "リスク統制: 維持率割れによる建玉の自動縮小",
        $"維持率 {Ratio(e.RatioBefore)} が閾値 {Ratio(e.Threshold)} に達したため、"
            + $"回復目標 {Ratio(e.RecoveryTarget)}（閾値+5pt）まで建玉を縮小しました"
            + $"（決済後 {(e.RatioAfter is { } after ? Ratio(after) : "建玉なし")}）。"
            + $"決済した建玉: {string.Join("、", e.Items.Select(Describe))}。"
            + "利用者の承認と AI の判断は介在していません（機械的規則）。",
        NotificationSeverity.Critical);

    // 04_report-templates の <n%> 表記（小数第 1 位・文化非依存）。"P1" は文化により空白が入るため使わない。
    private static string Ratio(decimal ratio) =>
        (ratio * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Describe(MaintenanceMarginReductionItem i) =>
        $"{i.Symbol}/{i.Market} {(i.PositionSide == TradeSide.Buy ? "ロング" : "ショート")} {i.Quantity} 株"
            + $"（必要証拠金 {i.RequiredMarginUsd.ToString("N2", CultureInfo.InvariantCulture)} USD）";

    private static string Describe(PositionDriftItem d) => d.Kind switch
    {
        PositionDriftKind.BrokerOnly => $"{d.Symbol}/{d.Market}: 台帳に無い建玉がブローカに {d.BrokerQuantity}",
        PositionDriftKind.LedgerOnly => $"{d.Symbol}/{d.Market}: ブローカに無い建玉が台帳に {d.LedgerQuantity}",
        _ => $"{d.Symbol}/{d.Market}: 台帳 {d.LedgerQuantity} ≠ ブローカ {d.BrokerQuantity}",
    };
}
