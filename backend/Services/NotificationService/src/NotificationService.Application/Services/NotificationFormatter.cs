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

    // FR-10, FR-17, #381, ADR-0022 決定2, IADR-0196: 為替レート源がフォールバックへ切り替わった。
    //
    // 🔴 **Warning であって Critical ではない。** 新規建ては止まっておらず、判断は続いている。
    // Critical にすると損切り到達（実際に止まる事象）と同じ重みになり、**本当に止まったときの
    // 通知が埋もれる**。ADR-0022 決定2 が求めるのは「黙って劣化させない」ことであって、
    // 「止まったのと同じ扱いにする」ことではない。
    //
    // **何が劣化したのかを本文に書く。** 「フォールバックした」だけでは受け手が影響を判断できない。
    public static NotificationMessage From(FxRateSourceFellBack e) => new(
        "為替: 情報源がフォールバックへ切替",
        $"{e.Quote} の為替レートを {e.SourceName}（優先度 {e.Rank}/{e.TotalSources}）から取得しています。"
            + "第一の情報源が使えていません。**鮮度が日次から週次へ悪化し得ます**"
            + "（新規建ては止まっていません・ADR-0022 決定2）。",
        NotificationSeverity.Warning);

    // 回復は Info。**期間を本文へ入れる**——「いつ戻ったか」だけでは、どれだけ劣化した状態で
    // 判断していたのかが分からない（ADR-0022 決定2 は期間の記録を求めている）。
    public static NotificationMessage From(FxRateSourcePrimaryRestored e) => new(
        "為替: 第一の情報源へ復帰",
        $"{e.Quote} の為替レートは第一の情報源（{e.SourceName}）へ戻りました。"
            + $"フォールバックしていた期間: {FormatDuration(e.FallbackDuration)}。",
        NotificationSeverity.Info);

    // 🔴 **「止まった」と読ませない。** 警告域は続行する（ADR-0022 決定5）。
    // 本文で「止まっていない」と明示し、**どこまで来たら止まるのか**（上限）も併記する——
    // それが無いと受け手は緊急度を判断できない。
    public static NotificationMessage From(FxRateStale e) => new(
        "為替: レートの鮮度警告",
        $"{e.Quote} の為替レートの観測が {e.AgeDays:0.#} 日前です"
            + $"（観測日 {e.AsOf:yyyy-MM-dd}・警告 {e.WarnThresholdDays:0.#} 日超）。"
            + $"**直近レートで続行しており新規建ては止まっていません**。"
            + $"{e.MaxAgeDays:0.#} 日を超えると新規建てを停止します（手仕舞いは止めません・ADR-0022 決定5）。",
        NotificationSeverity.Warning);

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

    // FR-09, FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
    // 強制買戻し（buy-in）の**事後推定**。イベント検知の供給元が無いため、建玉の消失を自らの決済指示
    //（約定履歴・処理中の決済承認）と突合して推定したものである。
    //
    // **必ず「推定」と明示する。** 決定4 の改訂は「**推定であることを運用者へ示す**（日報・通知の文言で
    //『強制買戻しと推定』と明示し、**確定事実として扱わない**）」と定めた。取り違えがあり得る以上、
    // 断定した通知は運用者に誤った確信を与える。突合に用いた数量を本文へ並べ、人が事後に検証できるようにする。
    //
    // 30 日の新規空売り禁止を伴う（利用者の承認を待たない統制の発動である）ため **Critical** とする。
    public static NotificationMessage From(BuyInInferred e) => new(
        "リスク統制: 強制買戻しと推定（空売り 30 日禁止）",
        $"{e.Symbol}/{e.Market} で**強制買戻し（buy-in）と推定**しました。"
            + $"台帳（自らの約定履歴）の空売り {e.LedgerShortQuantity} 株に対し、ブローカの空売りは "
            + $"{e.BrokerShortQuantity} 株（処理中の決済 {e.InFlightCloseQuantity} 株）であり、"
            + $"自らの決済指示で説明できない消失 {e.NewlyInferredQuantity} 株を検出しました。"
            + $"{e.BanUntil:yyyy-MM-dd} まで当該銘柄の新規空売りを禁止します。"
            + "これは**イベントとしての検知ではなく推定**です（確定した事実として扱わないでください）。"
            + "手動売買・外部要因による建玉の消失を取り違えている可能性があります。",
        NotificationSeverity.Critical);

    // 04_report-templates の <n%> 表記（小数第 1 位・文化非依存）。"P1" は文化により空白が入るため使わない。
    // #381, IADR-0196: フォールバック期間の表示。意味のある単位までで止める
    // （秒まで書くと受け手が桁を数えることになる）。監査台帳側と同じ規則。
    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? d.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " 日"
        : d.TotalHours >= 1 ? d.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " 時間"
        : d.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " 分";

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
