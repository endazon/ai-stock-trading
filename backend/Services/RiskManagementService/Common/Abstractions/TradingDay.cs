using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Common.Abstractions;

// FR-21, FR-10, FR-06, #463, IADR-0181, #337（#249 吸収）, IADR-0246: **取引日の導出の単一情報源。**
//
// 取引日には 2 つの基準があり、用途で使い分ける（混ぜると突き合わせが壊れる）。
//
//   1. **基準タイムゾーン（JST）**: `Of(instant)`。観測の到達（`IPositionObservationArrivalStore`）・
//      買戻し推定の期間（`BuyInInferenceRecord.InferredOn`）・`IClock.Today` が用いる。
//      **観測の到達も同じ基準で記録しなければ、「観測が届いた日」と「報告期間の日」がずれて
//      突き合わせが壊れる**（IADR-0181 の確定を維持する）。
//
//   2. **市場の現地取引日**: `Of(instant, market)`。**日次統制・日次集計**（当日損益・日次発注枠・
//      同日再エントリー・日次損失ロックアウトの当日判定）が用いる（#249 / IADR-0246）。
//      JST 固定で数えると、米国市場では JST 0 時（ET 10–11 時）に日次境界が走り、
//      **同一の米国セッションの途中でデイリーストップが解除される**。サマータイムで境界の現地時刻が
//      1 時間ずれるため、固定オフセットでは換算しない（MarketCalendar と同方針・TimeZoneInfo が吸収）。
public static class TradingDay
{
    private static readonly TimeZoneInfo BaseZone = Resolve("Tokyo Standard Time", "Asia/Tokyo");
    private static readonly TimeZoneInfo UsEasternZone = Resolve("Eastern Standard Time", "America/New_York");

    /// <summary>その瞬間が属する取引日（基準タイムゾーン＝JST の暦日）。観測到達・推定期間の基準。</summary>
    public static DateOnly Of(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, BaseZone).DateTime);

    /// <summary>
    /// その瞬間が属する<b>市場の現地取引日</b>（米国＝米国東部時間・日本＝JST の暦日）。
    /// 日次統制・日次集計の境界はこちらを使う（#249 / IADR-0246）。
    /// </summary>
    public static DateOnly Of(DateTimeOffset instant, Market market) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, ZoneOf(market)).DateTime);

    /// <summary>
    /// 表示・口座横断の保守判定用: サポートする全市場の現地取引日のうち<b>最も遅れている（小さい）日付</b>。
    /// 市場を特定できない文脈（稼働状態の表示）でロックアウトの解除を実際の統制より早く表示しないために使う。
    /// </summary>
    public static DateOnly EarliestCurrent(DateTimeOffset instant)
    {
        var japan = Of(instant, Market.Japan);
        var us = Of(instant, Market.UnitedStates);
        return japan < us ? japan : us;
    }

    // 未定義の市場は基準タイムゾーン（JST）に倒す（値を発明しない・既存挙動と同じ側）。
    private static TimeZoneInfo ZoneOf(Market market) =>
        market == Market.UnitedStates ? UsEasternZone : BaseZone;

    // クロスプラットフォームのため OS で TZ ID を切り替える（MarketCalendar・SystemClock と同方針）。
    private static TimeZoneInfo Resolve(string windowsId, string ianaId) =>
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? windowsId : ianaId);
}
