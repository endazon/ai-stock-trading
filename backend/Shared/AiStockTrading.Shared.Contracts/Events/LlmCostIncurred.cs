namespace AiStockTrading.Shared.Contracts.Events;

// NFR（費用）, FR-04, IADR-0055: 実 LLM 呼び出しの費用が発生した（取引判断の egress で計測）。
// 費用統制サービスが購読し CostCategory.Llm として月次台帳へ計上する（HTTP /costs/record は OwnerOnly のため使わない）。
// Category=Llm は事象名で含意する。Amount は円（単価未設定なら 0＝統制に影響しない安全既定）。
// 冪等性: 再配信され得るため、消費側は MassTransit の MessageId で重複排除する（IADR-0055 決定5）。
public record LlmCostIncurred(
    decimal Amount,
    DateTimeOffset At);
