using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-02, FR-10, #1169, IADR-0490 決定2: 定時サイクルの判断の DecisionId を (サイクルの起点イベント, 市場, 銘柄) から決定的に導く。
//
// なぜ要るのか: 定時サイクルは 1 通のメッセージで全銘柄を判断する。サイクルが打ち切られたり、発行の後・受信の完了（ack）の前に
// プロセスが落ちたりして同じ InformationCollected が再配送されると、全銘柄が判断し直される。DecisionId を毎回新規に採番すると、
// 下流の冪等（リスク管理の「承認済みの新規建ての再配送は再審査しない」・発注執行の DecisionId の一意予約）はどれも
// DecisionId で引くため、**再配送の判断を別の判断と見て重複発注し得る**。同じサイクル・同じ銘柄なら同じ DecisionId にすれば、
// 再配送で出た判断は下流の既存の冪等がそのまま止める（新しい重複排除の仕組みを足さない）。
//
// 形: RFC 9562 の版 8（独自の名前ベース）。SHA-256(名前空間 ‖ 名前) の先頭 16 バイトに版と変種のビットを立てる
// （版 5 の SHA-1 は解析器が弱い暗号として警告するため使わない）。
public static class ScheduledDecisionIds
{
    // 本サービスの定時判断に固有の名前空間（値に意味は無い。変えると同じサイクルの再配送で ID が変わるので変えない）。
    private static readonly Guid Namespace = new("4cccfc24-3fb4-415d-8c63-c71ed01eedc3");

    /// <summary>
    /// 定時サイクルの判断の DecisionId。同じ引数なら常に同じ値を返す。
    /// </summary>
    /// <param name="cycleEventId">サイクルの起点イベント（<c>InformationCollected.EventId</c>）。巡回ごとに一意。</param>
    /// <param name="symbol">銘柄。</param>
    /// <param name="market">市場。</param>
    /// <returns>
    /// 決定的な DecisionId。<paramref name="cycleEventId"/> が空（<see cref="Guid.Empty"/>）なら null を返す。
    /// 🔴 空の起点から導くと、その銘柄の以後の定時判断がすべて同じ ID になり、下流が 2 回目以降を再配送として
    /// 黙って捨てる（取引が止まる）。呼び出し側は null のとき従来どおり新規の ID を使う。
    /// </returns>
    public static Guid? For(Guid cycleEventId, string symbol, Market market)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (cycleEventId == Guid.Empty)
            return null;

        var name = string.Create(
            CultureInfo.InvariantCulture, $"scheduled\n{cycleEventId:D}\n{market}\n{symbol}");
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        Namespace.TryWriteBytes(input.AsSpan(0, 16), bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // 版 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // 変種 10（RFC 9562）
        return new Guid(bytes, bigEndian: true);
    }
}
