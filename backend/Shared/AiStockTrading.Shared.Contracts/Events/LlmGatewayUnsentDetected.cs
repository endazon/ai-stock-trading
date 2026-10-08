namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, FR-09, FR-11, #1267, IADR-0517: 取引判断の LLM 呼び出しで、ゲートウェイの `Sent=false`（送信しなかった応答）が
// **しきい値の回数だけ連続した**（1 回の連続＝1 件。NotificationService が Discord の Warning、AuditService が台帳へ記録する）。
//
// 🔴 **連続の間は 1 件だけ発行する。** 呼び出しごとに出すと、2026-10-07 の 132 件がそのまま 132 通の通知になる。
// 続いていた期間と件数は回復（`LlmGatewayUnsentRecovered`）が運ぶ。
//
// 🔴 **原因はゲートウェイの申告のまま運ぶ**（推測で「機密区分」と書かない。#1267 の誤帰属の再発防止）。
//   - FailureKind: `LlmGatewayUnsentKind` の名前（"EgressDenied" / "ProviderMissing" / "UpstreamError"）。未報告は null。
//   - UpstreamStatusCode: 上流の HTTP 状態コード。未報告は null。
//   - RoutingReason / GatewayText: ゲートウェイの理由と本文の説明の要約（1 行・切り詰め・秘密の伏せ字済み）。
//   原因の各値は**しきい値に達した呼び出しのもの**である（連続の途中で原因が変わり得る）。
public record LlmGatewayUnsentDetected(
    string Purpose,
    int ConsecutiveUnsent,
    string? FailureKind,
    int? UpstreamStatusCode,
    string? RoutingReason,
    string? GatewayText,
    DateTimeOffset FirstUnsentAt,
    DateTimeOffset OccurredAt);
