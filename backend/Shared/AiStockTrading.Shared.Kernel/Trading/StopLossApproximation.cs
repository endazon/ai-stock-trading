using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

/// <summary>
/// FR-03, FR-10, IADR-0030, IADR-0393, #957, IADR-0399: 損切りラインが<b>不明</b>な建玉を、平均取得単価と既定損切り比率から見積もる。
/// <para>
/// 送り手（リスク管理 <c>OpenPositionsService</c>。ラインの記録を持たないロット）と受け手（市場監視 <c>HttpPositionStore</c>。
/// 応答にラインが無い行）が<b>同じ式</b>で見積もるために 1 か所に置く。2 か所に置くと片方だけが変わり、同じ建玉のラインが
/// サービスごとに食い違う。
/// </para>
/// <para>
/// 🔴 <b>近似は「不明」を「無い」や 0 と読まないための見積りであり、実値ではない。</b> 呼び出し側は近似であることを
/// 記録・表示に残すこと（市場監視は <c>HeldPosition.StopLossApproximated</c>）。
/// </para>
/// </summary>
public static class StopLossApproximation
{
    /// <summary>
    /// 既定損切り幅比率 3%（前提条件 05_trading-assumptions §5 の「損切り幅3%」目安）。リスク管理の
    /// <c>TradingDefaults.DefaultStopLossRatio</c> はこの値を指す。
    /// </summary>
    public const decimal DefaultRatio = 0.03m;

    /// <summary>
    /// 近似の損切りライン。ロング（買い建て）は平均取得単価より下、ショート（売り建て）は上。
    /// </summary>
    public static decimal Approximate(TradeSide side, decimal averageEntryPrice) => side == TradeSide.Buy
        ? averageEntryPrice * (1m - DefaultRatio)
        : averageEntryPrice * (1m + DefaultRatio);
}
