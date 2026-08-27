namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, FR-06, FR-09, FR-11, ADR-0017 決定4, #335, IADR-0217:
// 指定した第 1 候補（ピン）ではないモデルで応答が返った＝**フォールバックが発火した**。
//
// ADR-0017 決定4 の目的は「**沈黙のフォールバックを作らない**」ことである。フォールバック機構の最大の危険は、
// 設定ミスや制度変更が「動いているように見える」ことで発見されなくなる点にある。本イベントが 3 経路のうち
// **②警告通知**（NotificationService が購読）と **③月報集計**（AuditService の台帳が供給元）を担う。
// ①報告書のメタ情報は報告書本体（ReportView.LlmModelUsage）が持つ。
//
// Outcome は `LlmAssignmentOutcome` の名前（"FallbackFired" / "Unassigned" / "Forbidden"）。
// **未割当・禁止モデルも本イベントで出す**——いずれも「意図した割当で応答していない」という同じ運用事実であり、
// 別イベントにすると通知の配線が二重になるだけで、埋もれない経路を増やすという目的に資さない。
public record LlmFallbackFired(
    string Purpose,
    string? ExpectedModel,
    string? EffectiveModel,
    string Outcome,
    DateTimeOffset OccurredAt);
