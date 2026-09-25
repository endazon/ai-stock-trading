using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;

namespace OrderExecutionService.Features.OrderExecution.QueryShortPermit;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂・同追記）, #967, IADR-0144 決定5, IADR-0425 決定3:
// 借株可否の照会を**節約して**返す（singleton）。リスク管理の審査（新規の売り建て）が `GET /order-execution/short-permit` で呼ぶ。
//
// 🔴 **ブローカーの上限は 30 秒あたり 10 回で、失敗した照会も枠を消費する**（IADR-0144 決定5 の実測: 失敗 3 回が同じ窓に入り
// 8 銘柄目が制限で落ちた）。素朴に毎回・失敗のたびに照会すると、照会そのものが枠を食って連鎖的に失敗する。そこで:
//   - 成功（許可／不許可）は (銘柄, 市場) ごとに <see cref="SuccessCacheTtl"/> の間キャッシュする。
//   - 失敗・欄の欠落も (銘柄, 市場) ごとに <see cref="FailureBackoff"/> の間キャッシュする（**失敗時に即時リトライしない**）。
//   - 実際に照会した回数（成功・失敗を問わない）を <see cref="BudgetWindow"/> の窓で数え、<see cref="BudgetPerWindow"/> 回に
//     達したら照会せず「分からない（rate-limited）」を返す（キャッシュしない＝窓が空けば次の要求で照会する）。
//     上限 10 回より 1 回少なく置くのは、ブローカー側の窓の起点と本プロセスの時計がずれても枠を超えないためである。
//   - 米国株以外は照会しない（ADR-0016 決定13。空売りの対象市場ではない）。発注先が照会を持たない（内蔵 paper）ときも照会しない。
// **どの「分からない」も受け手の側で拒否になる**（照会できないなら空売りしない＝決定3）。ここで許可へ倒す経路は無い。
public sealed class ShortPermitQueryService(
    IClock clock,
    ILogger<ShortPermitQueryService> logger,
    IShortPermitSource? source)
{
    public static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BudgetWindow = TimeSpan.FromSeconds(30);
    public const int BudgetPerWindow = 9;

    private readonly object _gate = new();
    private readonly Dictionary<(string Symbol, Market Market), (ShortPermitView View, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly Queue<DateTimeOffset> _attempts = new();

    public async Task<ShortPermitView> QueryAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (market != Market.UnitedStates)
            return Unknown(symbol, market, ShortPermitUnknownReasons.MarketNotSupported);
        if (source is null)
            return Unknown(symbol, market, ShortPermitUnknownReasons.BrokerNotSupported);

        var key = (symbol, market);
        var now = clock.UtcNow;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
                return cached.View;

            while (_attempts.Count > 0 && _attempts.Peek() <= now - BudgetWindow)
                _attempts.Dequeue();
            if (_attempts.Count >= BudgetPerWindow)
            {
                logger.LogWarning(
                    "借株可否の照会の予算（{Window} 秒あたり {Budget} 回）を使い切ったため照会しません symbol={Symbol}。借株可否は不明として扱います。",
                    BudgetWindow.TotalSeconds, BudgetPerWindow, symbol);
                return Unknown(symbol, market, ShortPermitUnknownReasons.RateLimited);
            }

            _attempts.Enqueue(now);
        }

        ShortPermitView view;
        TimeSpan ttl;
        try
        {
            var permit = await source.GetShortPermitAsync(symbol, market, cancellationToken).ConfigureAwait(false);
            (view, ttl) = permit switch
            {
                true => (new ShortPermitView(symbol, market, ShortPermitStatus.Permitted, null, now), SuccessCacheTtl),
                false => (new ShortPermitView(symbol, market, ShortPermitStatus.NotPermitted, null, now), SuccessCacheTtl),
                null => (Unknown(symbol, market, ShortPermitUnknownReasons.FieldMissing), FailureBackoff),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 呼び出し側の打ち切り（要求の中断）。結果を作らない・キャッシュしない。
        }
        catch (Exception ex)
        {
            // SIMULATE 口座では実測（IADR-0144 決定3）どおりならこれが常態である（`Get Margin Trading Data does not support Stocks in US Market`）。
            logger.LogWarning(ex,
                "借株可否の照会に失敗しました symbol={Symbol}。{Backoff} 秒は照会し直さず、借株可否は不明として扱います。",
                symbol, FailureBackoff.TotalSeconds);
            (view, ttl) = (Unknown(symbol, market, ShortPermitUnknownReasons.QueryFailed), FailureBackoff);
        }

        lock (_gate)
        {
            _cache[key] = (view, now + ttl);
        }

        return view;
    }

    private static ShortPermitView Unknown(string symbol, Market market, string reason) =>
        new(symbol, market, ShortPermitStatus.Unknown, reason, ObservedAt: null);
}
