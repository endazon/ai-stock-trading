namespace AiStockTrading.Shared.Infrastructure.Composable;

// NFR, FR-01, FR-10, #1532, IADR-0614: 外部の応答の Unix 秒を、例外を投げずに時刻へ写す。
// DateTimeOffset.FromUnixTimeSeconds は表せる範囲の外で ArgumentOutOfRangeException を投げる。外部の値をそのまま渡すと、
// 1 件の不正な値で巡回・情報源の取得ごと落ちる。負（1970 年より前）は外部の応答の時刻として不正とみなす。
public static class UnixTime
{
    public static bool TryFromSeconds(long seconds, out DateTimeOffset value)
    {
        if (seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            value = default;
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
