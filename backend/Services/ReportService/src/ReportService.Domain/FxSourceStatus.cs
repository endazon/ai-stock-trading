using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Report.Domain;

/// <summary>
/// 期間内の為替情報源の状態。
/// </summary>
/// <param name="FellBacks">フォールバックへ切り替わった事象。</param>
/// <param name="Restorations">第一の情報源へ復帰した事象（<b>期間を持つ</b>）。</param>
/// <param name="StaleWarnings">鮮度警告（5 日超）。</param>
/// <param name="PrimarySourceCredits">
/// 期間内に実際に使った情報源が要求するクレジット表記（ADR-0022 決定1）。
/// <b>使っていない情報源のクレジットを載せない</b>——FRED フォールバック中に日銀のクレジットを
/// 出すのは事実に反する（IADR-0196 決定4）。
/// </param>
public sealed record FxSourceStatus(
    IReadOnlyList<FxRateSourceFellBack> FellBacks,
    IReadOnlyList<FxRateSourcePrimaryRestored> Restorations,
    IReadOnlyList<FxRateStale> StaleWarnings,
    IReadOnlyList<string> PrimarySourceCredits)
{
    /// <summary>期間内に劣化を示す事象が 1 件も無かったか。</summary>
    public bool IsClean => FellBacks.Count == 0 && StaleWarnings.Count == 0;
}
