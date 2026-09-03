namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, ADR-0031（計画）決定3, IADR-0292: 日次上限は未実測のため、暫定手段として第三者観測
// 「約 300 回/日」を計画上の前提値として扱う。**これは実測値ではない**（推測値を実測として焼き込まない
// という IADR-0224 の原則を維持）。この暫定値を超える規模へ監視銘柄数・巡回頻度を上げる前に、
// 日次上限の実測を先行条件とする（ADR-0031 決定3）。
//
// 情報収集（Collection:Source:Finnhub）・実市況 4 サービス（MarketData:Finnhub）の双方が、
// 同じ構成セクション "Finnhub"（トップレベル。両者と別枠）を読む——暫定上限は用途に依らず 1 つの値である。
public sealed class FinnhubDailyVolumeGuardOptions
{
    public const string SectionName = "Finnhub";

    /// <summary>
    /// 暫定日次上限（回/日）。既定 300（第三者観測。ADR-0031 決定3）。日次上限が実測されたら、
    /// 実測値で本設定を上書きする（推測値の既定を残したまま「実測済み」の顔をさせない）。
    /// </summary>
    public int ProvisionalDailyLimit { get; set; } = 300;
}
