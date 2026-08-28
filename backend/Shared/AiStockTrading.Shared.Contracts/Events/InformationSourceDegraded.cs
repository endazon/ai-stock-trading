namespace AiStockTrading.Shared.Contracts.Events;

// FR-01, FR-09, FR-11, #336, ADR-0020 決定2・決定3: 情報源の欠測により**縮退へ入った**。
// 監査サービスが台帳へ記録し、日報・月報の期間集計が種別 × 期間で引く。
//
// 🔴 **遷移でのみ発行する。** 欠測は数サイクル続くため、巡回ごとに発行すると同じ事実で洪水になる
// （為替の劣化通知で同じ形を踏んだ・IADR-0196）。続いていること自体は日報が期間で示す。
//
// Category は欠測したソースのまとまり（"news" 等）。Behavior は ADR-0020 決定3 の 3 種
// （AbortCycle / LimitedDegradation / RecordAndNotifyOnly）のいずれか。
public record InformationSourceDegraded(
    string Category,
    string Behavior,
    IReadOnlyList<string> MissingSources,
    bool BlocksNewEntries,
    DateTimeOffset OccurredAt)
{
    /// <summary>
    /// 🔴 <b>手仕舞い・損切りは止まらない。</b> ADR-0020 決定2/決定3 は限定縮退でも決済を止めないと定める。
    /// <b>受け手が「縮退＝全停止」と読み違えないよう、イベント自身が明示する。</b>
    /// </summary>
    public bool ClosesAllowed => true;
}
