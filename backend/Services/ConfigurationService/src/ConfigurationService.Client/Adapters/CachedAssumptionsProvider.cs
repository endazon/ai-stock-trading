using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Configuration.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Configuration.Client.Adapters;

// FR-17, IADR-0060 決定 4/5: バージョン付き前提条件のキャッシュ付き解決器。
//
// 失効（決定 4）: AssumptionsChanged 購読による即時無効化 ＋ TTL（既定 5 分）の二段。イベントのみに頼ると
// ブローカ不達・取りこぼしで恒久的に古い値を掴むため TTL を併用する。
//
// フェイルセーフ（決定 5）: 取得不可時は ①過去に取得成功した値（last known good）→ ②既定値（Version=0）の順に倒す。
// 「常に既定へ倒す」を採らないのは、利用者が既定より厳しい上限へ変更していた場合に既定へ戻すと緩む側＝安全でない側へ
// 倒れるため。例外は出さない（消費側の巡回・要求処理を止めない）。
internal sealed class CachedAssumptionsProvider(
    IAssumptionsSource source,
    TimeProvider timeProvider,
    ILogger<CachedAssumptionsProvider> logger,
    TimeSpan ttl)
    : IAssumptionsProvider, IAssumptionsCacheInvalidator
{
    // 取得の多重発火を単一化する（同時に複数の巡回が失効を観測しても照会は 1 回）。
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VersionedAssumptions? _cached;
    private DateTimeOffset _cachedAt;
    private volatile bool _invalidated;

    public void Invalidate() => _invalidated = true;

    public async ValueTask<VersionedAssumptions> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetFresh() is { } fresh)
            return fresh;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 待っている間に別の呼び出しが取得済みかもしれない。
            if (TryGetFresh() is { } refreshed)
                return refreshed;

            var fetched = await source.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
            {
                _cached = fetched;
                _cachedAt = timeProvider.GetUtcNow();
                _invalidated = false;
                return fetched;
            }

            // 取得不可: last known good（陳腐化していても利用者の意図に最も近い）＞ 既定値。
            if (_cached is { } stale)
            {
                logger.LogWarning("全体前提条件を取得できません。最後に取得した版 v{Version} を使い続けます。", stale.Version);
                return stale;
            }

            logger.LogWarning("全体前提条件を一度も取得できていません。既定値へ倒します（未解決）。");
            return Unresolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>一度も取得できていないときの既定値（IADR-0060 決定 5・Version は未解決の番兵 0）。</summary>
    internal static VersionedAssumptions Unresolved { get; } =
        new(TradingAssumptionsDefaults.Create(), VersionedAssumptions.UnresolvedVersion);

    private VersionedAssumptions? TryGetFresh()
    {
        if (_invalidated || _cached is null)
            return null;

        return timeProvider.GetUtcNow() - _cachedAt < ttl ? _cached : null;
    }
}
