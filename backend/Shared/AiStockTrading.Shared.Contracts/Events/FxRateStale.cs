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
    DateTimeOffset OccurredAt);
