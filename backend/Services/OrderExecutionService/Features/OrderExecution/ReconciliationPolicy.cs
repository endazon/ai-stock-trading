namespace OrderExecutionService.Features.OrderExecution;

// #141, FR-05, IADR-0074: 滞留 Reserved の「滞留」判定境界を決める純関数。
//
// 本ポリシーが守る性質:「まだ処理中かもしれない予約を滞留と誤認しない」。滞留閾値は自動再配送
// （UseAiStockTradingRabbitMq の共通再試行＝2s/10s/30s＝約42秒）と `_error` 滞留の**外側**に置く。設定値を信用せず下限
// クランプする（設定ミスで in-flight の予約に触れないため＝IADR-0057 の at-most-once を守る）。
public static class ReconciliationPolicy
{
    /// <summary>既定の滞留閾値（時間）。再配送窓・手動再投入の桁違いに外側に置く。</summary>
    public const int DefaultStallThresholdHours = 24;

    /// <summary>
    /// 滞留閾値の下限（時間）。設定値がこれを下回っても、この値まで引き上げて用いる。
    /// 「安全性を設定値ではなく構造で担保する」ための最後の砦。
    /// </summary>
    public const int MinimumStallThresholdHours = 1;

    /// <summary>設定値に下限クランプを適用した実効滞留閾値（時間）を返す。</summary>
    public static int EffectiveStallThresholdHours(int configuredHours) =>
        Math.Max(configuredHours, MinimumStallThresholdHours);

    /// <summary>
    /// 滞留判定の境界を返す。これより **古い** Reserved だけが滞留＝リコンサイル対象になる。戻り値は常に
    /// <paramref name="now"/> より過去。極端な設定値で溢れる場合は安全側＝「何も対象にしない」に倒すため
    /// <see cref="DateTimeOffset.MinValue"/> を返す（どの予約も境界より古くならない）。
    /// </summary>
    public static DateTimeOffset StallCutoffFor(DateTimeOffset now, int configuredHours)
    {
        var hours = EffectiveStallThresholdHours(configuredHours);

        try
        {
            return now - TimeSpan.FromHours(hours);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
