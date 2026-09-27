using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 4: as-of 入力の供給を包み、**当時の監視銘柄（(e)）**を埋める。
//
// - 内側が入力を返し、その監視銘柄が null（未供給）のときだけ供給口を引く。内側が既に一覧を渡していれば上書きしない。
// - 再構成できない時点は一覧を null のまま、理由を (e) の申告へ載せる（プロンプトの節は「不明」・その判断は合否から外れる）。
// - 🔴 **記録の対象銘柄（Stage0Recording:Symbols）は使わない**（ADR-0044 決定 3）。本型は銘柄の集合を受け取らない。
// - 同じ AsOf の結果はこのインスタンスの中で 1 回だけ引く（銘柄ごとに同じ時点を照会し直さない）。記録はスコープごとに作られる。
public sealed class WatchlistAsOfDecisionInputProvider(IAsOfDecisionInputProvider inner, IAsOfWatchlistSource source)
    : IAsOfDecisionInputProvider
{
    private readonly Dictionary<DateOnly, AsOfWatchlist> _byAsOf = [];

    public async Task<AsOfDecisionInput?> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var input = await inner.GetAsync(symbol, market, asOf, cancellationToken).ConfigureAwait(false);
        if (input is null || input.Watchlist is not null)
            return input;

        if (!_byAsOf.TryGetValue(asOf, out var watchlist))
        {
            watchlist = await source
                .GetWatchlistAtAsync(AsOfWatchlistInstant.EndOfUtcDay(asOf), cancellationToken)
                .ConfigureAwait(false);
            _byAsOf[asOf] = watchlist;
        }

        return input.WithWatchlist(watchlist.Symbols, watchlist.Reason);
    }
}
