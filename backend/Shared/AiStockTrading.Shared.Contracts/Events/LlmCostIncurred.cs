namespace AiStockTrading.Shared.Contracts.Events;

// NFR（費用）, FR-04, IADR-0055: 実 LLM 呼び出しの費用が発生した（取引判断の egress で計測）。
// 費用統制サービスが購読し CostCategory.Llm として月次台帳へ計上する（HTTP /costs/record は OwnerOnly のため使わない）。
// Category=Llm は事象名で含意する。Amount は円（単価未設定なら 0＝統制に影響しない安全既定）。
// 冪等性: 再配信され得るため、消費側はメッセージ ID（Wolverine の Envelope.Id）で重複排除する（IADR-0055 決定5）。
//
// NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218: Purpose は**費用の対象範囲を決める唯一の入力**である。
// 月次 LLM 費用上限（15,000 円）の対象は取引判断サイクルのみであり、報告書生成・情報収集は対象外として
// 別カテゴリへ計上する（`LlmCostScope.IsGoverned`）。**null（従来の形）は上限の対象へ倒す**——過小計上を作らない。
// Model は実際に使用したモデル（IADR-0122 決定1）。単価解決の根拠であり、月報の利用実績にも用いる。
// 既定値つきの追加であるため既存の発行側・購読側は非破壊で通る。
public record LlmCostIncurred(
    decimal Amount,
    DateTimeOffset At,
    string? Purpose = null,
    string? Model = null);
