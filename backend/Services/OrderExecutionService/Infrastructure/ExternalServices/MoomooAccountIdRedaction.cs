using OrderExecutionService.Features.OrderExecution.ProbeOrderFee;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-11, #1135, #1148, IADR-0473, IADR-0476, #1000, IADR-0482 決定5: 口座 ID の伏せ方（単一の実装）。
//
// 従来は MMApiMoomooTradeClient（発注の面を持つ取引クライアント）の static メンバだった。実弾口座の読み取り専用の照会
// （RealReadOnly/）も同じ伏せ方を使うが、照会側から発注クライアントの型を参照させない（IADR-0482 決定2 の構造的な切り離し）ため、
// 本体をここへ移した。**伏せ方そのものは 1 文字も変えていない**（MMApiMoomooTradeClient の同名メンバは本クラスへ委譲する）。
public static class MoomooAccountIdRedaction
{
    // 末尾 2 桁以外を伏せる（2 桁以下は全部伏せる）。
    // 名前に "Account" を含むメソッドの戻り値は、伏せた後の値でも CodeQL（cs/cleartext-storage-of-sensitive-information）が
    // 「機密を平文で保存」と判定する（名前による推定）ため、本体の名前は TailOnly とする（IADR-0473）。
    public static string TailOnly(ulong value)
    {
        var digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return digits.Length <= 2 ? "****" : "****" + digits[^2..];
    }

    // 文中に口座 ID の全桁が現れたら伏せた形へ置き換える（OpenD の retMsg を出力へ流すため）。
    public static string? RedactAccountId(string? text, ulong accountId)
    {
        if (string.IsNullOrEmpty(text) || accountId == 0)
            return text;
        return text.Replace(
            accountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TailOnly(accountId),
            StringComparison.Ordinal);
    }

    // FR-11, #1148, IADR-0476: OpenD の retMsg から口座 ID の全桁を除く。
    // - 口座一覧で見た口座 ID があれば、その ID だけを末尾 2 桁以外伏せる（RedactAccountId と同じ伏せ方）。
    //   注文 ID・日付など他の数字は残す（拒否理由として読めるように）。
    // - 口座一覧をまだ読めていない（接続時の口座一覧の照会の失敗）なら、伏せる値が分からないため検証口と同じく
    //   6 桁以上の数字の並びを伏せる（OrderFeeProbeCommand.MaskLongDigitRuns。2 通り目の伏せ方を作らない）。
    public static string RedactRetMsg(string? retMsg, IReadOnlyList<ulong> knownAccountIds)
    {
        ArgumentNullException.ThrowIfNull(knownAccountIds);
        if (string.IsNullOrEmpty(retMsg))
            return retMsg ?? string.Empty;
        if (knownAccountIds.Count == 0)
            return OrderFeeProbeCommand.MaskLongDigitRuns(retMsg);
        var text = retMsg;
        // 大きい順＝桁の多い順。長い ID の中に短い ID が部分一致しても、先に長い方を伏せる（長い方の頭を残さない）。
        foreach (var accountId in knownAccountIds.OrderByDescending(id => id))
            text = RedactAccountId(text, accountId)!;
        return text;
    }
}
