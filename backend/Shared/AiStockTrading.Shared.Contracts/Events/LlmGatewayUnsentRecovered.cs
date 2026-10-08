namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, FR-09, FR-11, #1267, IADR-0517: 通知済みの `Sent=false` の連続（`LlmGatewayUnsentDetected`）の後、
// ゲートウェイが**再び送信した**（Sent=true の応答が届いた）。
//
// 🔴 **連続の始まりと件数を本イベントが運ぶ**（受け手が別のイベントを探して引き算する形にしない。
// `InformationSourceRecovered` / `FxRateSourcePrimaryRestored` と同じ規律）。
//   - UnsentCalls: 連続した Sent=false の件数（しきい値に達する前の分を含む）。
//   - FirstUnsentAt: 連続の最初の Sent=false の時刻。
//   - UnsentByPurpose: 連続全体の用途別の件数（IADR-0517 決定4）。旧い発行元は null。
public record LlmGatewayUnsentRecovered(
    int UnsentCalls,
    DateTimeOffset FirstUnsentAt,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, int>? UnsentByPurpose = null)
{
    /// <summary>送信できなかった期間。</summary>
    public TimeSpan UnsentDuration => OccurredAt - FirstUnsentAt;
}
