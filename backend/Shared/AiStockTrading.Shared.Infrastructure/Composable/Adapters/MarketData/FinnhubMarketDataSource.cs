using System.Collections.Concurrent;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-10, FR-03, FR-16, #158, IADR-0068: Finnhub /quote による実市況（現在値）。IADR-0066 が後続へ送った
// 「IMarketDataSource の実市況実装」の本体で、既定 no-op（NoOpMarketDataSource）の差し替え先。
// 選択は MarketDataSourceFactory（構成 MarketData:Provider・API キーで opt-in）が行い、既定では注入されない。
//
// 本クラスは FinnhubQuoteSnapshot → Quote の薄い写像＋「取得不可（null）」への翻訳のみを担う（HTTP は
// FinnhubQuoteClient。IADR-0068 決定 2）。ポートの契約どおり、取得できない事象はすべて null に落とす:
// 呼び出し側（市場監視の巡回・リスク管理の補充・報告書のドラフト）はいずれも「null＝当該銘柄をスキップ」で
// 設計されており、通信エラーを例外で返すと 1 銘柄の失敗が巡回全体（＝損切り検知）を落とす。
public sealed class FinnhubMarketDataSource(
    FinnhubQuoteClient client,
    ILogger<FinnhubMarketDataSource> logger) : IMarketDataSource
{
    // 市場ごとに「非対応」の警告を 1 回だけ出すための記録（巡回のたびのログ氾濫を避ける）。
    private readonly ConcurrentDictionary<Market, byte> _warnedMarkets = new();

    public async Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        // IADR-0068 決定 5: Finnhub 無料枠の /quote は米国株のみ。要求を出さずに取得不可とする
        // （レート枠を無駄に消費しない）。日本株の現在値は引き続き取得不可＝含み 0 に倒れる。
        if (market != Market.UnitedStates)
        {
            if (_warnedMarkets.TryAdd(market, 0))
            {
                logger.LogWarning(
                    "Finnhub の現在値は米国株のみ対応のため、市場 {Market} の銘柄は取得できません（含み損益は 0 として扱われます・IADR-0068）。",
                    market);
            }

            return null;
        }

        FinnhubQuoteSnapshot? snapshot;
        try
        {
            snapshot = await client.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 停止要求は「取得不可」ではない
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // 市況断・応答が JSON でない（プロキシのエラーページ等）。取得不可として扱い、呼び出し側の巡回を止めない。
            logger.LogWarning(ex, "Finnhub の現在値を取得できません（銘柄 {Symbol}）。この銘柄をスキップします。", symbol);
            return null;
        }

        if (snapshot is null)
            return null;

        // Finnhub は未知の銘柄に対し 200 で全項目 0 を返す。現在値 0 は値ではなく「無い」の表現として扱う
        // （0 を現在値として通すと、当該建玉の含み損益が取得原価ぶんの全損として評価される）。
        if (snapshot.Current <= 0)
        {
            logger.LogWarning(
                "Finnhub の現在値が 0 です（銘柄 {Symbol}）。未知の銘柄の可能性があるため取得不可として扱います。", symbol);
            return null;
        }

        return new Quote(symbol, market, snapshot.Current, snapshot.AsOf);
    }
}
