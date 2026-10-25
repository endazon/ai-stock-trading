using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 NFR, FR-10, FR-05, UC-02, UC-06, ADR-0050 決定1・3, #1446（親 #1423 M-2）, IADR-0575:
// **同じ建玉を売る決済の「読む → 送る → 記録する」を、(銘柄, 市場, 決済の方向) ごとに 1 本ずつにする。**
//
// 判断の決済（承認の受け口）は送る前に建玉照会 → 処理中の決済を差し引き（IADR-0461）、S1 の決済（到達の購読・常駐ガード）は
// 送る前に処理中の判断の決済を取り消す（IADR-0466）。どちらも「他方の記録（ExecutionRecord）」を読んで数量を決めるが、
// 記録は**送った後**に書かれる。2 本が並行に走ると、後発は先発の送信中（記録の前）を読めず、両方が全量を送る
// ——SIMULATE は 2 本目を「建玉不足」で拒否するが、未約定の売りが売れる数量を押さえない証券会社では**二重に売る**。
// 承認の受信の直列化（Wolverine の Inline・1 リスナ。実測）は**承認のキューの中**しか直列にしない。S1 は別のキュー（StopLossTriggered）と
// 常駐の巡回から来るので、それだけでは塞がらない（IADR-0575 の比較）。
//
// 本番は DB の advisory lock（PostgresClosePositionLock。プロセス・レプリカをまたぐ）。本ファイルの InProcess は単体の試験と、
// 関係 DB でない DbContext（試験の InMemory）の組み立て（ホストごとに 1 つ）で使う。**static の共有インスタンスは置かない**——
// 置くと、並列に走る無関係な試験（別々のホスト）どうしが同じ鍵で待ち合う（PR #1447 の CI で実測）。本番の組み立ては T-10-3008 が固定する。
// 🔴 **入れ子にしない。** 同じ鍵を同じ流れの中で 2 回取ると、別の接続（別のセッション）として待ち合い、待ちの上限まで止まる。
// 決済を送る 4 経路（承認の決済・S1・保護喪失の成行手仕舞い・ガードの成行手仕舞い）はどれも他の経路を呼ばない（IADR-0575 決定 3）。
public interface IClosePositionLock
{
    /// <summary>
    /// 鍵の決済の直列化を取る。返したハンドルを破棄すると外れる。取れなければ <see cref="ClosePositionLockUnavailableException"/>
    /// （待ちの上限・DB の失敗）。取消は <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(ClosePositionKey key, CancellationToken cancellationToken);
}

/// <summary>決済の直列化の鍵（銘柄・市場・決済の方向）。</summary>
public readonly record struct ClosePositionKey(string Symbol, Market Market, TradeSide CloseSide)
{
    /// <summary>
    /// advisory lock の 64bit 鍵。文字列の SHA-256 の先頭 8 バイト（<c>string.GetHashCode</c> はプロセスごとに変わるので使わない）。
    /// <c>BitConverter</c> はプラットフォームのバイト順で読む。稼働・CI の x64 / ARM64 はどちらもリトルエンディアンであり、T-10-3006 の固定値はその前提である。
    /// 🔴 書式を変えると、入れ替えで新旧が並走する間だけ直列化が 2 系統に割れる。変えない。衝突は直列化が過剰になるだけで害は無い。
    /// </summary>
    public long AdvisoryKey
    {
        get
        {
            var text = $"AST:close-position:{Market}:{Symbol.ToUpperInvariant()}:{CloseSide}";
            return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
        }
    }

    public override string ToString() => $"{Symbol}/{Market}/{CloseSide}";
}

/// <summary>決済の直列化を取れなかった（待ちの上限・DB の失敗）。決済は送っていない。</summary>
public sealed class ClosePositionLockUnavailableException(ClosePositionKey key, string reason, Exception? inner = null)
    : Exception($"決済の直列化を取れませんでした（{key}）: {reason}", inner)
{
    public ClosePositionKey Key { get; } = key;
}

/// <summary>決済の直列化の待ちの上限（承認・S1・ガードで共通）。</summary>
public static class ClosePositionLockLimits
{
    /// <summary>
    /// 🔴 待ちの上限の下限（1 ミリ秒）。PostgreSQL の <c>lock_timeout = 0</c> は「無期限」であり、1 ミリ秒未満を渡すと
    /// ミリ秒へ丸めた 0 が意図と逆の無期限待ちになる。両実装の組み立てで拒否する（T-10-3006）。
    /// </summary>
    public static readonly TimeSpan MinimumWaitLimit = TimeSpan.FromMilliseconds(1);

    /// <summary>待ちの上限を検証する（1 ミリ秒未満・無期限〔<see cref="Timeout.InfiniteTimeSpan"/>〕は拒否）。</summary>
    public static TimeSpan Validate(TimeSpan waitLimit) =>
        waitLimit >= MinimumWaitLimit
            ? waitLimit
            : throw new ArgumentOutOfRangeException(
                nameof(waitLimit), waitLimit, "決済の直列化の待ちの上限は 1 ミリ秒以上にする（0 は PostgreSQL で無期限になる）。");

    /// <summary>
    /// 待ちの上限。先発が握る間に行うのは建玉照会・注文照会・取消・発注（OpenD の応答待ちは既定 15 秒）で、通常は数秒で外れる。
    /// これを超えて待つのは先発が止まっている場合であり、待ち続けると承認のリスナ（1 本）と常駐の巡回まで止まる。
    /// </summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);
}

/// <summary>
/// プロセス内の決済の直列化（鍵ごとのセマフォ）。<b>同じインスタンスを渡した経路どうしだけが直列になる</b>
/// （ホストの組み立ては DI の singleton を 1 つ渡す。試験は直列にしたい経路へ同じインスタンスを明示で渡す）。
/// </summary>
public sealed class InProcessClosePositionLock(TimeSpan? waitLimit = null) : IClosePositionLock
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();

    private readonly TimeSpan _waitLimit = ClosePositionLockLimits.Validate(waitLimit ?? ClosePositionLockLimits.WaitLimit);

    public async Task<IAsyncDisposable> AcquireAsync(ClosePositionKey key, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(key.AdvisoryKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(_waitLimit, cancellationToken).ConfigureAwait(false))
            throw new ClosePositionLockUnavailableException(key, $"{_waitLimit} 待っても外れませんでした");
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
