namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, FR-01, ADR-0020 決定2, #1081, IADR-0455: 直近の収集巡回におけるニュース系（カテゴリ単位）の状態。
// 取引判断はこれをプロンプトへ「ニュース: 取得済み／欠測／未提供（未構成）」と明示する（欠測を無言で空データとして渡さない）。
//
// 🔴 **0 を有効値にしない。** 既定値（0）や範囲外の値を「取得済み」と読ませないためである
// —— 受け手は未定義の値を「不明」として扱う。
public enum NewsCollectionStatus
{
    /// <summary>ニュース源の 1 つ以上から取得できた（新着が 0 件でも取得は成功している）。</summary>
    Fetched = 1,

    /// <summary>試行したニュース源がすべて失敗した（欠測。ADR-0020 決定2 の限定縮退）。</summary>
    Outage = 2,

    /// <summary>ニュース源が 1 つも構成されていない（試行 0 件。欠測には数えない・IADR-0220）。</summary>
    NotConfigured = 3,
}
