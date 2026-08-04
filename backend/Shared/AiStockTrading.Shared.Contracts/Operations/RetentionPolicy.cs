namespace AiStockTrading.Shared.Contracts.Operations;

// NFR（運用）, #137, IADR-0059: 重複排除ストア（processed_messages / order_dispatch_reservations の終端行）を
// パージする境界を決める純関数。費用統制・発注執行の双方の Worker が同じ境界を使うための単一情報源である。
//
// 本ポリシーが守る性質は 1 つ:「再配信の猶予内にある行を絶対に対象にしない」。これを破ると重複排除が
// 素通りし、費用の二重計上（IADR-0055 決定5）や二重発注（IADR-0057）が起きる。したがって設定値を信用せず、
// 下限 7 日でクランプする（設定ミスが冪等性を壊せない構造にする＝IADR-0059 決定3）。
public static class RetentionPolicy
{
    /// <summary>
    /// 既定の保持期間（日）。自動再配送（UseAiStockTradingRabbitMq の共通再試行＝2s/10s/30s の3回＝約42秒）と
    /// `_error` キューからの手動再投入（インシデント対応＝時間〜数日）の、桁違いに外側に置く（IADR-0059 決定2）。
    /// </summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>
    /// 保持期間の下限（日）。設定値がこれを下回っても、この値まで引き上げて用いる。
    /// 「安全性を設定値ではなく構造で担保する」ための最後の砦（IADR-0059 決定3）。
    /// </summary>
    public const int MinimumRetentionDays = 7;

    /// <summary>設定値に下限クランプを適用した実効保持期間（日）を返す。</summary>
    public static int EffectiveRetentionDays(int configuredDays) => Math.Max(configuredDays, MinimumRetentionDays);

    /// <summary>
    /// パージ境界を返す。これより **古い** 終端行だけがパージ対象になる。戻り値は常に <paramref name="now"/> より過去。
    /// </summary>
    public static DateTimeOffset CutoffFor(DateTimeOffset now, int configuredDays)
    {
        var days = EffectiveRetentionDays(configuredDays);

        try
        {
            return now - TimeSpan.FromDays(days);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            // 極端な設定値（int.MaxValue 日等）で DateTimeOffset の下限を割る／TimeSpan が溢れる場合。
            // 安全側＝「何も消さない」に倒すため、表現可能な最古の時刻を返す（どの行も cutoff より古くならない）。
            return DateTimeOffset.MinValue;
        }
    }
}
