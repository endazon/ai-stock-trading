namespace TradeDecisionService.Features.TradeDecision;

// UC-01, FR-09, FR-07, IADR-0096, #210: 確定済み日報の方針が無く取引サイクルを見送った際に、確定を促す通知を促す出力ポート。
// 既定は NoOp（何もしない＝現行のログのみ）。Worker が opt-in で実発行（DailyPolicyUnconfirmed の publish・営業日 dedup）へ
// 差し替える。取引判断のクリティカルパス外であり、実装は fail-safe（例外・失敗が判断経路を止めない）で扱われることを前提とする。
public interface IDailyPolicyUnconfirmedNotifier
{
    // 日報未確定による見送りを通知する。同一営業日内の重複抑止は実装（発行側）の責務。キャンセルは伝播してよい。
    Task NotifyAsync(CancellationToken cancellationToken = default);
}
