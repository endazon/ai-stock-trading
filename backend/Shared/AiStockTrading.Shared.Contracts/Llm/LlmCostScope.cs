namespace AiStockTrading.Shared.Contracts.Llm;

// NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218:
// 月次 LLM 費用上限（15,000 円）の**対象範囲**を用途（purpose）で判別する純関数。
//
// 計画 §6.1 の条文:
//   「月次 LLM 費用上限 15,000 円の対象は**取引判断サイクルのみ**である。抑制動作（80% で定時サイクル間隔を延長、
//    100% で停止）も取引判断サイクルにのみ働く。報告書生成・情報収集の LLM 費用は上限の対象外とし、
//    **抑制動作も行わず、月報に実績を記載する**。」
//
// 🔴 実装上の注意（計画 §6.1 が名指しした事故）:
//   報告書生成の費用を同じカウンタに積むと、100% 到達で報告書生成が止まる。日報が確定しないと翌営業日の
//   取引が止まる（UC-01 の事前条件）ため、**費用統制が取引を止める連鎖**が生じる。カウンタは対象範囲どおり分離する。
//
// 🔴 用途が不明（null / 空）のときは**上限の対象へ倒す**。
//   費用統制の危険側は**過小計上**であり、対象外へ倒すと月次上限が構造的に効かなくなる（IADR-0122 決定3 と同じ判断）。
//   計画が挙げる連鎖は purpose が既知の `report-*` のときにだけ起こり、その経路は名指しで除外される。
//   加えて用途を持たない `LlmCostIncurred` は取引判断サービスが発行した従来の形であり、
//   対象へ倒すことで既存データの解釈も変わらない。
public static class LlmCostScope
{
    /// <summary>この用途の費用を月次 LLM 費用上限へ積むか（＝抑制動作の対象か）。</summary>
    public static bool IsGoverned(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) || LlmPurposes.IsTradeDecision(purpose);
}
