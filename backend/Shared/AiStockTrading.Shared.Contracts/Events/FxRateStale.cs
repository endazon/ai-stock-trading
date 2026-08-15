namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-17, FR-09, FR-11, #381, ADR-0022 決定4・決定5, IADR-0174, IADR-0196: 為替レートの観測が
// **警告しきい値（既定 5 日）を超えて古い**。**直近レートで続行しており、新規建ては止まっていない。**
//
// 🔴 **これは「止まった」通知ではない。** ADR-0022 決定5 は警告と絶対上限の役割を分けている——
// 警告は**気づくため**、上限（30 日）は**統制が意味を失った状態で発注しないため**である。
// 本イベントを「停止」と読み違えると、運用者が不要な介入をする。書式でも明示すること。
//
// **遷移ではなく状態である**（鮮度切れは数日続く）。したがって発行側は
// **営業日単位で抑止する**（IADR-0096 決定4 と同じ）。**日をまたげば再通知する**——
// 気づくための警告であり、1 度出して終わりでは役に立たない。
public record FxRateStale(
    string Quote,
    DateTimeOffset AsOf,
    double AgeDays,
    double WarnThresholdDays,
    double MaxAgeDays,
    DateTimeOffset OccurredAt,
    bool EntryBlocked = false)
{
    /// <summary>
    /// 🔴 <b>新規建てが止まっているか</b>（#381 停止側・IADR-0198 決定1）。
    /// <para>
    /// <c>false</c> = 警告域（5 日超〜30 日以下）。<b>直近レートで続行し新規建ても止めない。</b><br/>
    /// <c>true</c> = 絶対上限超（30 日超）。<b>新規建てを停止する。手仕舞いは止めない。</b>
    /// </para>
    /// <para>
    /// <b>停止側を別イベントにしない。</b> 同じ「鮮度が閾値を超えた」事実であり、
    /// 分けると受け手が 2 つ購読し抑止も 2 系統になる——<b>同じ事象で 2 種類の通知が飛ぶ。</b>
    /// </para>
    /// </summary>
    public bool EntryBlocked { get; init; } = EntryBlocked;
}
