using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using InformationCollectionService.Domain;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, FR-08, #1084, IADR-0456: KB へ保存済みの収集内容の指紋（保持期間つき・上限つき・プロセス内）。
//
// 基盤（DocumentService）の POST /documents には外部キーによる同定も upsert も無い（ADR-0001: 基盤は改修しない）。
// 巡回（300 秒）ごとに同じ内容を取り直すため、保存側で「同じ内容を既に保存したか」を覚えて二度目を送らない。
//
// 🔴 指紋は**全項目**（種別・源・銘柄・表題・本文・公開時刻・URL）から作る。URL だけ・源＋銘柄＋公開時刻だけでは足りない:
//   FRED は系列ごとに URL が一定で観測値だけが変わり、Finnhub の現在値は URL を持たない。一部の項目で同定すると
//   新しい観測を「保存済み」と誤って捨てる（取りこぼしは重複より悪い）。内容が 1 文字でも違えば別文書として保存する。
//
// 保持はプロセス内に限る（再起動で空になり、取得中の内容を 1 回だけ保存し直す）。残余は IADR-0456 に記す。
public sealed class SavedContentFingerprints(TimeProvider timeProvider)
{
    // 保持期間。ニュースの取得期間（既定 1 日）と取引判断の KB 検索の足切り（既定 168 時間）の長い方を覆う。
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    // 件数の上限（メモリの天井）。現在値は取引時間中に巡回ごと新しい観測になるため、保持期間内でも件数が伸びる。
    // 1 件は 128 ビットの鍵＋時刻で、上限まで溜まっても数 MB に収まる。超えたら古い順に捨てる（重複保存へ倒れるだけ）。
    public const int Capacity = 100_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<UInt128, DateTimeOffset> _savedAt = [];
    private readonly Queue<(UInt128 Key, DateTimeOffset SavedAt)> _order = new();

    public int Count
    {
        get
        {
            lock (_gate)
                return _savedAt.Count;
        }
    }

    public static UInt128 Of(CollectedInformation item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // 各項目を長さ前置で連結する（区切り文字の衝突で別内容が同じ入力列にならないように）。null と空文字も区別する。
        var buffer = new StringBuilder();
        Append(buffer, item.Kind.ToString());
        Append(buffer, item.Source);
        Append(buffer, item.Symbol);
        Append(buffer, item.Title);
        Append(buffer, item.Content);
        Append(buffer, item.PublishedAt.ToUniversalTime().ToString("O"));
        Append(buffer, item.Url);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(buffer.ToString()));
        return BinaryPrimitives.ReadUInt128BigEndian(hash);
    }

    public bool Contains(UInt128 key)
    {
        lock (_gate)
        {
            Prune(timeProvider.GetUtcNow());
            return _savedAt.ContainsKey(key);
        }
    }

    public void Add(UInt128 key)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            Prune(now);
            if (!_savedAt.TryAdd(key, now))
                return;

            _order.Enqueue((key, now));
            while (_savedAt.Count > Capacity && _order.TryDequeue(out var oldest))
                _savedAt.Remove(oldest.Key);
        }
    }

    // 保持期間を過ぎた指紋を古い順に捨てる。鍵は保存済みでない時だけ追加するため、待ち行列と辞書は 1 対 1 に対応する。
    private void Prune(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest) && now - oldest.SavedAt >= Retention)
        {
            _order.Dequeue();
            _savedAt.Remove(oldest.Key);
        }
    }

    private static void Append(StringBuilder buffer, string? value)
    {
        if (value is null)
        {
            buffer.Append("-1:");
            return;
        }

        buffer.Append(value.Length).Append(':').Append(value);
    }
}
