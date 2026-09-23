using System.Globalization;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace NotificationService.Features.Notifications;

// FR-09, UC-01, UC-02, UC-06: ドメインイベントを種別ごとのテンプレートで NotificationMessage に整形する純関数群。
public static class NotificationFormatter
{
    // 取引実行（約定）。全量約定は Info、それ以外（拒否・取消等）は注意喚起の Warning。
    public static NotificationMessage From(OrderExecuted e) => new(
        "取引実行",
        $"約定 {e.Status} 数量{e.FilledQuantity}@{e.AveragePrice}（OrderId={e.OrderId}・DecisionId={e.DecisionId}）",
        e.Status == OrderStatus.Filled ? NotificationSeverity.Info : NotificationSeverity.Warning);

    // 🔴 FR-09, FR-10, UC-06, #847, IADR-0357: 手仕舞いが未約定残を残して終わった（失効・取消・拒否）。
    //
    // 従来はこの事象が `OrderExecuted` の「約定 Expired 数量0@0」という一般的な Warning にしかならず、
    // **それが手仕舞いだったことも、何株が残ったかも書かれていなかった**（#847 の受け入れ基準 3）。
    //
    // 🔴 **Warning であって Critical ではない。** Critical は「実際に統制が破れた」事象
    //（保護喪失・損切りライン到達）に取っておく——手仕舞いが流れたこと自体は統制の破れではない。
    // ただし Info にもしない: **手仕舞えなかった建玉が実在し、無保護なら翌日へ持ち越される**。
    public static NotificationMessage From(PositionCloseAbandoned e) => new(
        "手仕舞いが約定せず終了",
        $"{e.Symbol}/{e.Market} {e.Side} 数量{e.ApprovedQuantity} の手仕舞いが {e.TerminalStatus} で終了しました"
            + $"（約定 {e.FilledQuantity}・**未決済 {e.RemainingQuantity}**）。"
            + "建玉はこの数量ぶん残っています（当日注文は引け後に失効します）。"
            + "**逆指値なしの建玉はこのまま翌日へ持ち越されます。**"
            + $"手仕舞い直すか、建玉を確認してください（DecisionId={e.DecisionId}）。",
        NotificationSeverity.Warning);

    // リスク統制発動: 発注拒否（理由つき）。
    public static NotificationMessage From(OrderRejected e) => new(
        "リスク統制: 発注拒否",
        $"{e.Intent.Symbol} 拒否: {string.Join(",", e.Reasons)}（DecisionId={e.DecisionId}）",
        NotificationSeverity.Warning);

    // リスク統制: 損切りライン到達の検知（#331・逆指値一本化）。
    // FR-10, ADR-0040 決定1, #820（#826 項目 2）, IADR-0344 決定7: 決済するかは**建玉ごとの損切りの実行機構**で決まるが、
    // 到達を検知する市場監視は手法を知らない。🔴 「ブローカーの逆指値が決済する」と断定すると S1 / S2 の建玉で誤りになるため、
    // 手法ごとの帰結を列挙する（S1 の決済・拒否は SoftwareStopExecuted、S2 は免除の通知が建玉を特定して伝える）。
    public static NotificationMessage From(StopLossTriggered e) => new(
        "リスク統制: 損切りライン到達",
        $"{e.Symbol} 損切り SL={e.StopLossPrice}（現在 {e.Price}・数量 {e.Quantity}・建玉 {e.PositionSide}）。"
            + "決済は建玉の損切りの実行機構によります: S0＝ブローカー側の逆指値が実行（システムは発注しない）／"
            + "S1＝システムが成行で決済（別途「ソフトウェア逆指値」の通知）／"
            + "S2＝**システムもブローカーも決済しない（手動で決済してください）**。",
        NotificationSeverity.Critical);

    // FR-05, ADR-0002（OpenD 常駐・SPOF）, #331, IADR-0211: 発注の見送り。
    // 🔴 **「再試行されない」を明示する。** キューイングしない裁定のため、この注文は破棄され、
    // 再発注は次の取引判断からになる。建玉は増えておらずリスクは発生していないため Warning
    // （実際に止まる事象〔損切り到達・保護喪失〕の Critical を埋もれさせない）。
    //
    // 🔴 #864, IADR-0355 決定3: **建玉を照会できずに見送った決済だけは Critical** である。
    // 他の見送りは「建玉が増えなかった」＝リスクが発生していないが、これは**建玉が残ったまま手仕舞いが出なかった**
    // ことを意味し、他に鳴る通知が 1 本も無い（乖離イベントは「乖離を確認できたとき」しか出ない）。
    // 建玉が無いことを確認して見送った側（BrokerPositionAbsent）は Warning のまま —— 同時に乖離の
    // Critical が鳴るので、二重に Critical を立てると本当に止まった事象が埋もれる。
    public static NotificationMessage From(OrderDispatchForgone e) => new(
        "発注見送り: " + ReasonLabel(e.Reason),
        $"{e.Intent.Symbol}/{e.Intent.Market} {e.Intent.Side} 数量{e.Intent.Quantity} の発注を見送りました"
            + $"（理由: {ReasonLabel(e.Reason)}・DecisionId={e.DecisionId}）。"
            + "**この注文は再試行されません**（キューイングしない・再発注は次の取引判断から）。",
        e.Reason == OrderDispatchForgoneReason.BrokerPositionsIndeterminate
            ? NotificationSeverity.Critical
            : NotificationSeverity.Warning);

    // FR-10, UC-02, #331, IADR-0210: 保護逆指値の発注（エントリー同時 or 失効後の再発注）。
    // 統制が設計どおり働いた記録であり Info。
    public static NotificationMessage From(ProtectiveStopPlaced e) => new(
        "保護逆指値を発注",
        $"{e.CloseIntent.Symbol}/{e.CloseIntent.Market} {e.CloseIntent.Side} 数量{e.CloseIntent.Quantity}"
            + $" トリガー {e.TriggerPrice}（試行 {e.Attempt}・StopOrderId={e.StopOrderId}）。",
        NotificationSeverity.Info);

    // FR-10, UC-02, #331, IADR-0210: 保護逆指値が成立しない（未受理・失効）——建玉を持たないための対処。
    // 対処が成功しても**利用者の承認なしに建玉が消えた/注文が取り消された**事象であり Critical。
    // Remediation=None は逆指値なしの建玉が残っている可能性があり、人手対応を明示的に求める。
    // 🔴 #848, IADR-0117（2026-09-19 追記・改定 7）: CloseDispatchIndeterminate は**「解消に失敗」とは言わない**。
    // 成行手仕舞いは送信済みで、証券会社側で生きているかもしれない。「失敗した」と読んだ人は手で成行を重ね、
    // 二重決済でショート化する。伝えるのは「送った・届いたか分からない・重ねる前に確かめよ」である。
    // 🔴 #857, IADR-0369: CloseRejected は**「手仕舞いました」と言ってはならない**。
    // 証券会社が確認できる形で拒否しており、**建玉は残っている**。件名も本文も「解消した」と読ませない。
    public static NotificationMessage From(ProtectiveStopCoverageLost e) => new(
        e.Remediation switch
        {
            ProtectiveStopRemediation.CloseDispatchIndeterminate =>
                "リスク統制: 保護逆指値が成立せず、成行手仕舞いの結果が未確認",
            ProtectiveStopRemediation.CloseRejected =>
                "リスク統制: 保護逆指値が成立せず、成行手仕舞いも拒否（建玉が残存）",
            _ => "リスク統制: 保護逆指値が成立せず建玉を解消",
        },
        $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 逆指値が"
            + $"{(e.Cause == ProtectiveStopLossCause.RejectedAtEntry ? "エントリー時に未受理" : "滞留中に失効（再発注不可）")}のため、"
            + e.Remediation switch
            {
                ProtectiveStopRemediation.EntryCancelled => "エントリー注文を取り消しました（建玉は生じていません）。",
                ProtectiveStopRemediation.PositionClosed => "建玉を成行で手仕舞いました（逆指値なしの建玉を持たない規律・FR-10）。",
                ProtectiveStopRemediation.CloseDispatchIndeterminate =>
                    "建玉の成行手仕舞いを**送信しましたが、結果を確認できていません（届いたか不明）**。"
                    + "システムは注文を重ねません。**手で決済を重ねる前に、証券会社の画面で注文と建玉を確認してください**"
                    + "（手仕舞いが生きていれば二重決済になります）。"
                    // #848, IADR-0117（改定 9）: 据え置きが続くあいだ約 1 時間ごとに再通知する。再通知を
                    // 「もう 1 本送った」と読ませない（同じ CloseDecisionId＝同じ 1 本の成行）。
                    + "この通知は予約が解決されるまで約 1 時間ごと（と再起動のたび）に繰り返します。"
                    + "**同じ CloseDecisionId の通知は同じ 1 本の成行であり、新しい発注ではありません。**"
                    + $"CloseDecisionId={e.CloseDecisionId}",
                // 🔴 #857, IADR-0369: 「確認できた拒否」——送った成行は**生きていない**（届いたか不明とは別である）。
                // 二重決済の心配なく手で手仕舞える一方、**建玉は無保護のまま残っている**。
                // 🔴 PR #916 監査 F1, IADR-0369（2026-09-24 追記）: 後半の約束は**原因（Cause）で分ける**。
                // 巡回・撃ち直し・上限・再通知は滞留側（LapsedInFlight・ProtectiveStopGuard）だけが持つ。
                // エントリー同時の経路（RejectedAtEntry・ResolveUnprotectedEntryAsync）は保護記録を作らない
                // （protectiveStops.Save は受理の側だけ）——巡回も撃ち直しも再通知も無く、この通知は 1 回きりである。
                // 無い約束を書くと、読んだ人は「システムが見ている」と信じて待つ（#857 と同じ壊れ方）。
                ProtectiveStopRemediation.CloseRejected =>
                    "建玉の成行手仕舞いを**証券会社が拒否しました（確認できた拒否）。建玉は残っています**。"
                    + (e.Cause == ProtectiveStopLossCause.RejectedAtEntry
                        ? "**逆指値なしの建玉が残っているため、証券会社の画面で建玉を確認し、手で手仕舞ってください**"
                            + "（時間外・数量の制約などで拒否されます）。"
                            + "**エントリー時の経路には保護記録が無く、システムはこの建玉を巡回しません。"
                            + "手仕舞いの撃ち直しも行わず、この通知も繰り返しません（届くのはこの 1 回だけです）。**"
                            + "原因を取り除いてもシステムは再試行しないため、手で手仕舞うまで無保護のままです。"
                        : "**逆指値なしの建玉が残っているため、証券会社の画面で建玉を確認し、手で手仕舞うか原因を取り除いてください**"
                            + "（時間外・数量の制約などで拒否されます）。"
                            + "システムは同じ理由での撃ち直しを 3 回で打ち切りますが、**保護記録は閉じず巡回を続けます**"
                            + "（この通知は解決するまで約 1 時間ごと（と再起動のたび）に繰り返します）。"),
                _ => "**建玉の解消にも失敗しました。逆指値なしの建玉が残っている可能性があります。直ちに確認してください。**",
            },
        NotificationSeverity.Critical);

    // FR-10, FR-12, FR-11, ADR-0040 決定1（S2）, #819, IADR-0342 決定6: 保護逆指値の**免除**（ペーパーで免除）。
    // 🔴 **Warning であって Critical ではない。** 利用者が moomoo SIMULATE で選んだ手法どおりの結果であり、
    // 統制が破れた事象（保護喪失の Critical）と同じ重みにすると、本当に破れたときの通知が埋もれる。
    // ただし Info にもしない——**逆指値なしの建玉が実在する**ことは読み落とされてはならない。
    // 🔴 本文に「損切りライン到達でもシステムは決済しない」を書く。書かないと、損切り到達の通知
    // （決済はブローカー側の逆指値が実行します）を読んだ利用者が「逆指値で切られる」と誤解する。
    public static NotificationMessage From(ProtectiveStopWaived e) => new(
        "リスク統制: 保護逆指値をペーパーで免除（" + StopLossMethodLabel(e.Method) + "）",
        $"{e.Symbol}/{e.Market} {e.Side} 数量{e.Quantity}: 損切りの実行機構 {StopLossMethodLabel(e.Method)}"
            + $"（逆指値なしの建玉を許容）が選ばれているため、{e.Provider} で保護逆指値を発注せず建玉を保持します。"
            + $"損切りライン {(e.StopLossPrice is { } price ? price.ToString(CultureInfo.InvariantCulture) : "なし")}"
            + " に到達しても**システムもブローカーも決済しません**（実弾口座では選べない手法です・"
            + $"EntryDecisionId={e.EntryDecisionId}）。",
        NotificationSeverity.Warning);

    // FR-10, FR-12, FR-11, FR-03, ADR-0040 決定1（S1）, #820, #909, IADR-0344 決定8・追記(13), IADR-0380 決定6:
    // ソフトウェア逆指値の配置。
    // 🔴 **Warning。** 本番の機構（ブローカー側逆指値）ではなく、**システムが止まっている間は決済されない**ことと、
    // **保護が通常取引時間しか働かない**ことを読み落とさせない（閉場中の建玉は次の寄りまで無保護である）。
    public static NotificationMessage From(SoftwareStopArmed e) => new(
        "リスク統制: ソフトウェア逆指値を配置（S1）",
        $"{e.Symbol}/{e.Market} {e.Side} 数量{e.Quantity}: 損切りの実行機構 S1（ソフトウェア逆指値）が選ばれているため、"
            + $"{e.Provider} へ保護逆指値を発注せず、損切りライン {Invariant(e.StopLossPrice)} への到達で"
            + "システムが成行で決済します。**ブローカー側に保護は無く、システム停止中は決済されません**"
            + "。🔴 **保護が働くのは通常取引時間（米東 9:30–16:00）のあいだだけです** —— 閉場中は到達を検知せず、"
            + "夜間・寄り前の急落からは守られません（#909・IADR-0380）"
            + $"（実弾口座では選べない手法です・EntryDecisionId={e.EntryDecisionId}）。",
        NotificationSeverity.Warning);

    // FR-10, FR-12, FR-11, ADR-0040 決定1（S1）, #820, IADR-0344 決定5・決定8: ソフトウェア逆指値の発動結果。
    // 決済の発注・未約定エントリーの取消は設計どおりの帰結で Warning、**決済が拒否され続けた（無保護の建玉が残る）ときだけ Critical**。
    public static NotificationMessage From(SoftwareStopExecuted e) => e.Outcome switch
    {
        SoftwareStopOutcome.ClosePlaced => new(
            "リスク統制: ソフトウェア逆指値で成行決済",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 損切りライン {Invariant(e.StopLossPrice)} への到達（検知 {Invariant(e.TriggeredPrice)}）で"
                + $"成行の決済注文を発注しました（試行 {e.Attempt}・OrderId={e.CloseOrderId}・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Warning),
        SoftwareStopOutcome.EntryCancelled => new(
            "リスク統制: ソフトウェア逆指値でエントリーを取消",
            $"{e.Symbol}/{e.Market}: 損切りライン {Invariant(e.StopLossPrice)} への到達（検知 {Invariant(e.TriggeredPrice)}）時点で"
                + $"エントリーが未約定だったため取り消しました（建玉は生じていません・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Warning),
        // #820 の監査, IADR-0344 決定5-7: エントリーの発注記録が無い孤立行。**決済は 1 株も出していない。**
        // 記録の数量で決済すると同じ銘柄の別の建玉を売るため、猶予を過ぎたら出さずに閉じて人手へ回す。
        SoftwareStopOutcome.EntryMissing => new(
            "リスク統制: ソフトウェア逆指値のエントリー記録が見つかりません",
            $"{e.Symbol}/{e.Market}: 損切りライン {Invariant(e.StopLossPrice)} へ到達しましたが、"
                + "エントリーの発注記録が猶予を過ぎても見つかりませんでした。"
                + "**決済は出していません。建玉が残っているかを確認し、必要なら手動で決済してください**"
                + $"（EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Critical),
        // #820 の 4 巡目監査, IADR-0344 追記(4) 決定9: 到達したのに決済できない状態が猶予を過ぎた。再試行は続いている。
        SoftwareStopOutcome.CloseStalled => new(
            "リスク統制: ソフトウェア逆指値が到達後も決済できていません",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 損切りライン {Invariant(e.StopLossPrice)} へ到達（検知 {Invariant(e.TriggeredPrice)}）しましたが、"
                + "猶予を過ぎても成行の決済を発注できていません（接続断・建玉照会不能・エントリーの取消待ちなど）。"
                + "**建玉が無保護で残っている可能性があります。直ちに確認し、必要なら手動で決済してください**"
                + $"（再試行は続けます・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Critical),
        // #820 の 5 巡目監査, IADR-0344 追記(5): 外部要因で保護対象を減らした（決済は出していない）。
        SoftwareStopOutcome.ProtectionReduced => new(
            "リスク統制: 外部要因により保護対象を減らしました",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 建玉が外部要因（手動決済・強制決済・ブローカー側逆指値の約定など）で"
                + "減ったため、保護記録が守る株数をその分だけ減らしました（**決済は出していません**）。"
                + "ブローカー側の逆指値を持つ記録が 0 になった場合は、その逆指値を取り消します。"
                + $"建玉と保護の対応をご確認ください（損切りライン {Invariant(e.StopLossPrice)}・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Warning),
        // #820 の 8 巡目監査, IADR-0344 追記(8) 決定3: 帳簿では守っているのに 1 株も動かせない状態が猶予を過ぎた。
        // 行は Active・帳簿も無傷なので、知らせなければ無音のまま保護が失われる（到達の有無に依らない）。
        SoftwareStopOutcome.ProtectionSuspended => new(
            "リスク統制: ソフトウェア逆指値が保護を再開できていません",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 建玉の照会と保護記録の主張が食い違ったまま猶予を過ぎ、"
                + "この記録は**損切りラインへ到達しても 1 株も決済できない状態**が続いています（決済は出していません）。"
                + "**建玉が無保護で残っている可能性があります。直ちに確認し、必要なら手動で決済してください**"
                + $"（損切りライン {Invariant(e.StopLossPrice)}・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Critical),
        // #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: どの保護記録も主張していない建玉がある（検知のみ・是正はしない）。
        SoftwareStopOutcome.UnattributedPosition => new(
            "リスク統制: どの保護記録も主張していない建玉があります",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: この銘柄・方向の建玉のうち {e.Quantity} 株を、"
                + "どの保護記録も主張していません（他の実行機構の建玉・手動で建てた建玉・"
                + "受理後に取り消された決済の残りなど）。"
                + "**ソフトウェア逆指値はこの建玉を決済しません。手動で決済するか、保護を掛け直してください**"
                + "（この銘柄ではソフトウェア逆指値の新規建ても見送られます）"
                + $"（損切りライン {Invariant(e.StopLossPrice)}・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Warning),
        // 🔴 #833 項目1, IADR-0389 決定7: 受理だけで完了させた決済が未約定のまま終端した（保護記録を再武装した）。
        // moomoo の模擬取引の注文は当日限りで、受理された決済が 0 約定のまま失効し得る。
        SoftwareStopOutcome.CloseUnfilled => new(
            "リスク統制: ソフトウェア逆指値の決済が約定しないまま終了しました",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 受理された成行の決済注文が {e.Quantity} 株を約定しないまま"
                + "取消・失効・拒否で終了しました（模擬取引の注文は当日限りです）。"
                + "**その株数は建玉に残っています。保護記録を再武装し、次の巡回で決済を撃ち直します**"
                + "——撃ち直しが通らない場合は手動で決済してください"
                + $"（試行 {e.Attempt}・OrderId={e.CloseOrderId}・損切りライン {Invariant(e.StopLossPrice)}"
                + $"・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Critical),
        _ => new(
            "リスク統制: ソフトウェア逆指値の決済が拒否されました",
            $"{e.Symbol}/{e.Market} 数量{e.Quantity}: 損切りライン {Invariant(e.StopLossPrice)} へ到達しましたが、"
                + $"成行の決済注文が {e.Attempt} 回目まで受理されませんでした。"
                + "**建玉が無保護で残っています。直ちに確認し、必要なら手動で決済してください**"
                + $"（次の損切りライン到達で再試行します・EntryDecisionId={e.EntryDecisionId}）。",
            NotificationSeverity.Critical),
    };

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    // #819, IADR-0342: 計画の手法 ID（S0〜S3）。enum 名だけでは計画の表と突き合わせにくい。
    private static string StopLossMethodLabel(StopLossExecutionMethod method) => method switch
    {
        StopLossExecutionMethod.BrokerStopOrder => "S0",
        StopLossExecutionMethod.SoftwareStop => "S1",
        StopLossExecutionMethod.NoProtectiveStop => "S2",
        StopLossExecutionMethod.AlternativeBrokerOrderType => "S3",
        _ => method.ToString(),
    };

    private static string ReasonLabel(OrderDispatchForgoneReason reason) => reason switch
    {
        OrderDispatchForgoneReason.BrokerUnavailable => "ブローカー（OpenD）へ接続できません",
        OrderDispatchForgoneReason.StopLossPriceMissing => "損切り価格がなく保護逆指値を張れません",
        OrderDispatchForgoneReason.StopOrderUnsupported => "ブローカーが逆指値に対応していません",
        // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 対処は「設定を S0 へ戻す」であり、他の 3 つと違う。
        OrderDispatchForgoneReason.StopLossMethodNotPermitted =>
            "損切りの実行機構が moomoo SIMULATE 以外では選べない手法です（設定を S0 へ戻してください）",
        // 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355: 決済をブローカーの実建玉と突き合わせて止めた 2 つ。
        // 対処は他と違い「台帳とブローカーのどちらが正しいかを確かめる」であって、再発注ではない。
        OrderDispatchForgoneReason.BrokerPositionAbsent =>
            "ブローカーに決済できる建玉がありません（送れば保有 0 からの売り＝裸のショートになります）",
        OrderDispatchForgoneReason.BrokerPositionsIndeterminate =>
            "ブローカーの建玉を照会できません（不明のまま決済を送りません）",
        // FR-10, #820 の 8 巡目監査, IADR-0344 追記(8) 決定4: 対処は「先に手仕舞ってから切り替える」であり、他と違う。
        OrderDispatchForgoneReason.UnattributedPosition =>
            "同一銘柄・同方向に帰属不明の建玉があります（S1 はその建玉を自分の損切りラインで売らないために武装しません。"
                + "先に手仕舞ってから切り替えてください）",
        _ => reason.ToString(),
    };

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
    // 🔴 #381 停止側: **警告と停止を読み分けられるようにする。** 同じイベント型だが、
    // `EntryBlocked` で件名も本文も変える——**同じ文面だと「止まった」ことが埋もれる。**
    public static NotificationMessage From(FxRateStale e) => e.EntryBlocked
        ? new NotificationMessage(
            "為替: レートが上限超のため新規建てを停止",
            $"{e.Quote} の為替レートの観測が {e.AgeDays:0.#} 日前です"
                + $"（観測日 {e.AsOf:yyyy-MM-dd}・上限 {e.MaxAgeDays:0.#} 日）。"
                + "**新規建てを停止しました。手仕舞い・損切りは止めていません**（ADR-0022 決定5）。",
            NotificationSeverity.Critical)
        : StaleWarning(e);

    // 🔴 鮮度切れのレートで実際に決済した。**取引そのものの通知であり、状態の通知ではない。**
    public static NotificationMessage From(PositionClosedWithStaleFxRate e) => new(
        "為替: 鮮度切れのレートで決済した",
        $"{e.Symbol}/{e.Market} を数量 {e.Quantity} で決済しました。"
            + $"換算率 {e.FxRateToBase}（観測日 {e.RateAsOf:yyyy-MM-dd}・{e.AgeDays:0.#} 日前）。"
            + "**計画どおり手仕舞いは止めていません**が、**円換算額は実勢から乖離し得ます**。",
        NotificationSeverity.Warning);

    private static NotificationMessage StaleWarning(FxRateStale e) => new(
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
        $"{e.Kind} 報告書 {e.PeriodKey} が確定しました（{ConfirmerOf(e)}・前提条件 v{e.AssumptionsVersion}）。",
        NotificationSeverity.Info);

    // FR-09, UC-03, ADR-0003, IADR-0240 決定11, #774: 確定者の表示。
    //   代理確定（Discord Bot 経由）: 「<操作した利用者>・<認可の主体のクライアント> 経由」——**両方を見せる**。
    //   操作者が分からない（空／報告書サービスの最終の倒し先 `unknown`）: 「確定者不明」——内部の既定値を生で出さない。
    //   それ以外（利用者本人のトークン・`client:<azp>`）: そのまま。
    private static string ConfirmerOf(ReportConfirmed e)
    {
        var actor = string.IsNullOrWhiteSpace(e.Actor) || e.Actor == "unknown" ? "確定者不明" : e.Actor;
        return string.IsNullOrWhiteSpace(e.AuthorizedBy) ? actor : $"{actor}・{e.AuthorizedBy} 経由";
    }

    // FR-06/07/09, UC-03〜05, IADR-0116, #280: 報告書ドラフトの提示（＝確定依頼）。
    // 要約は発行側でサニタイズ済み（IADR-0116 決定3/4）。確定は利用者のみが行う（ADR-0003）ため本文で確定を促し、
    // 版番号を載せる（確定 API は版番号付き冪等・IADR-0024。通知だけで期待版が分かるようにする）。
    //
    // 🔴 #840, #866, IADR-0352 決定 5: **入力が未供給のまま出来上がったドラフトの提示は Warning で出す。**
    // 警告文は要約の本文に入っているが、重大度が Info のままでは定常の提示通知に埋もれ、欠落に気付かないまま
    // 確定され得る（確定された日報の方針は翌日の取引に効く）。「埋もれない経路で出す」は LlmFallbackFired
    // （ADR-0017 決定4-(2)）で既に採っている形である。**未供給が無ければ従来どおり Info**——毎回警告にすると
    // 警告の意味が消える。判定は発行側と共有する印（契約アセンブリの定数）で行い、イベントの形は変えない。
    public static NotificationMessage From(ReportDraftPresented e) => new(
        "報告書ドラフト（承認待ち）",
        $"{e.Summary}\n\n"
            + $"内容を確認のうえ確定してください（{e.PeriodKey}・版 {e.Version}）。"
            + "確定するまで取引方針は変わりません。",
        HasUnsuppliedInputs(e) ? NotificationSeverity.Warning : NotificationSeverity.Info);

    // #866: 要約に未供給の警告行が含まれているか（発行側 ReportSummary.Build が同じ定数で組み立てる）。
    private static bool HasUnsuppliedInputs(ReportDraftPresented e) =>
        e.Summary?.Contains(ReportSummaryMarkers.UnsuppliedWarningPrefix, StringComparison.Ordinal) == true;

    // NFR（費用）, FR-09: 費用しきい値到達（間隔延長/停止）。停止（Halted）は Critical、間隔延長（Throttled）は Warning。
    public static NotificationMessage From(CostThresholdReached e) => new(
        $"費用統制: {e.State}",
        $"{e.Category} 費用が月次上限の {e.Percent:F0}% に到達しました（{e.Month}・{e.State}）。",
        e.State == "Halted" ? NotificationSeverity.Critical : NotificationSeverity.Warning);

    // FR-04, FR-06, FR-09, ADR-0017 決定4-(2), #335: フォールバック発火の**警告**（可視化 3 経路の②）。
    //
    // 🔴 計画の明文: 「フォールバックの発火を警告として通知する。**恒常的に発火しているなら設定が誤っている**ため、
    // 埋もれない経路で出す。」——沈黙のフォールバックを作らないことが目的であり、Info では埋もれる。
    public static NotificationMessage From(LlmFallbackFired e) => new(
        $"LLM 割当逸脱: {e.Purpose}",
        $"用途 {e.Purpose} が割当（{e.ExpectedModel ?? "なし"}）ではなく {e.EffectiveModel ?? "不明"} で応答しました（{e.Outcome}）。"
        + "恒常的に発火している場合は割当設定を確認してください。",
        NotificationSeverity.Warning);

    // FR-04, FR-09, UC-01, ADR-0017 決定2, #335: 割当モデル不可による取引判断の見送り。
    //
    // 🔴 **「モデルが使えないのに発注が出ない＝バグ」ではない。** 計画は「金融取引において『判断できないので
    // 見送る』は正常な結果であり、『別のモデルで代替して判断する』より安全である」と明記している。
    // よって Critical にはしない（Critical にすると運用が障害として扱い、善意のフォールバック追加を招く）。
    // 一方、沈黙のスキップにもしない（同決定2）ため Warning で通知する。
    public static NotificationMessage From(TradeDecisionSkipped e) => new(
        "取引判断の見送り: 割当モデルが利用できません",
        $"用途 {e.Purpose} の割当モデル（{e.ExpectedModel ?? "なし"}）が使えないため取引判断を実行せず、発注も行いませんでした"
        + $"（理由 {e.Reason}・実際 {e.EffectiveModel ?? "不明"}）。**設計上の正常な結果**です（フォールバック禁止）。",
        NotificationSeverity.Warning);

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
            + "自動是正は行いません。内容を確認し、必要なら決済または証券会社側で調整してください。"
            // #849, IADR-0350: 検知で止めない。システム外の売買が原因なら、利用者の承認つきで台帳を観測へ合わせられる。
            + "システム外の売買で台帳の建玉が実態より多い場合は、利用者の操作で台帳へ取り込めます"
            + "（POST /risk-controls/position-drift/adopt・理由必須）。",
        NotificationSeverity.Critical);

    // FR-09, FR-10, FR-11, UC-06, #849, IADR-0350: 利用者が承認した乖離の取り込み。
    // 取引台帳が**約定以外で動く唯一の操作**であるため Critical とし、誰が・なぜ・何株から何株へを必ず出す。
    // 🔴 **実現損益を記録していないこと**を本文に明記する。推定を含む場合は「推定・台帳へ未記録」と添える
    // ——数値だけを出すと確定した損益に読める。
    public static NotificationMessage From(PositionDriftAdopted e) => new(
        "リスク統制: 建玉の乖離を台帳へ取り込み",
        $"{e.Symbol}/{e.Market} の台帳の建玉を {e.LedgerQuantityBefore} → {e.LedgerQuantityAfter} へ合わせました"
            + $"（ブローカの観測 {e.BrokerQuantity}・観測 {e.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z）。"
            + $"操作者 {e.Actor}・理由: {e.Reason}。"
            + "システム外の売買の約定価格は分からないため、**実現損益は記録していません**"
            + "（当日損益・連敗・段階ゲートの実績には入りません）。"
            + (e.EstimatedPnlInBase is { } estimate && e.ReferencePrice is { } reference
                ? $"参考: 現在値 {reference.ToString(CultureInfo.InvariantCulture)} で評価した損益は "
                    + $"{estimate.ToString("N2", CultureInfo.InvariantCulture)} USD（推定・台帳へ未記録）。"
                : "現在値を取得できなかったため、参考の推定損益もありません。")
            + "当該銘柄にブローカー側の保護注文（逆指値）が残っていないか、証券会社のアプリで確認してください。",
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

    // FR-09, FR-19, FR-10, FR-11, UC-06, #341, ADR-0025 決定2, ADR-0028 決定3, IADR-0241:
    // GFV（Good Faith Violation）違反を 1 件計上した。詳細設計07 §通知設計の「リスク統制の発動（**ガード違反**）」。
    //
    // 🔴 **発行された時点で、発注前の GFV 回避ガードをすり抜けた買付が現に約定している**（契約コメントが明記）。
    // ガードが正しく働けば 1 件も発行されない事象であり、発行は**ガードの不具合または口座観測の欠落**を示す。
    // 積み上がると新規取引が止まり、**停止の解除窓口は Discord の `/gfv clear` だけ**である（ADR-0028 決定3）。
    // 通知が無ければ、止まったことも解除が要ることも利用者へ届かない。よって **Critical**。
    //
    // 🔴 **「停止した」と断定しない。** 停止のしきい値は Risk 側が持ち、本イベントは件数を運ばない
    // （断定すると、止まっていないのに止まったと読ませる）。
    //
    // 🔴 **限界を本文へ書く。** ADR-0025 §理由 のとおり、これは「ブローカの GFV カウンタの写し」ではなく
    // 「**自らのガードの失敗回数**」である。両者が一致する保証はない。
    public static NotificationMessage From(GoodFaithViolationRecorded e) => new(
        "リスク統制: GFV 違反を計上（発注前ガードのすり抜け）",
        $"{e.Symbol}/{e.Market} の買付（注文 {e.OrderId}・{e.PurchaseAmountInBase.ToString("N2", CultureInfo.InvariantCulture)} USD）を "
            + $"GFV 発生 1 件として計上しました（取引日 {e.OccurredOn:yyyy-MM-dd}・判定に用いた決済済み資金 "
            + $"{SettledCash(e.SettledCashInBase)}）。"
            + "**発注前の GFV 回避ガードをすり抜けた買付が約定しています**（ガードの不具合または口座観測の欠落）。"
            + "これは**自らのガードの失敗回数**であり、ブローカ側の GFV カウンタの写しではありません（ADR-0025）。"
            + "違反が積み上がると新規取引が停止します。停止の解除は Discord の `/gfv clear` のみです"
            + "（違反記録そのものは消えません）。",
        NotificationSeverity.Critical);

    // #424 の表示規約: **null は「未供給」であって 0 ではない。** 0 と書くと「残高が 0 だった」と読まれる。
    private static string SettledCash(decimal? settledCashInBase) =>
        settledCashInBase is { } cash
            ? cash.ToString("N2", CultureInfo.InvariantCulture) + " USD"
            : "未供給";

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
