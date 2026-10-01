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
        // 🔴 #957, IADR-0399 決定3: 銘柄が無い照会は出さずに取得不可とする。FinnhubQuoteClient は銘柄 null で
        // Uri.EscapeDataString が ArgumentNullException を投げ、それは下の catch の対象外（例外が呼び出し側の巡回を落とす）。
        // 空・空白は照会しても意味のある値が返らない（レート枠を無駄に消費する）。
        if (string.IsNullOrWhiteSpace(symbol))
        {
            logger.LogWarning(
                "銘柄が空の現在値照会です（市場 {Market}）。照会せずに取得不可として扱います（呼び出し側の入力を確認してください）。",
                market);
            return null;
        }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 停止要求は「取得不可」ではない
        }
        catch (OperationCanceledException ex)
        {
            // FR-02, FR-10, #1133, IADR-0469 決定 2: 呼び出し側のトークンでない打ち切り（HttpClient.Timeout。
            // FinnhubHttpTimeouts.Quote）は「取得できない」である。例外のまま返すと、OperationCanceledException を
            // 停止要求として素通しする呼び出し側（判断の現在値・補充の巡回）が判断や巡回ごと落ちる。
            logger.LogWarning(ex, "Finnhub の現在値の照会が打ち切られました（銘柄 {Symbol}）。この銘柄をスキップします。", symbol);
            return null;
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

        // FR-02, FR-04, ADR-0044 決定1, #1035, IADR-0451: 同じ応答の日中文脈（前日終値・始値・高値・安値）も写す。
        // 🔴 0 は「無い」の表現（場前の始値・未知の銘柄）であり、値として通さない（前日比が -100% になる）。null＝不明。
        return new Quote(
            symbol, market, snapshot.Current, snapshot.AsOf,
            PreviousClose: KnownOrNull(snapshot.PreviousClose),
            Open: KnownOrNull(snapshot.Open),
            High: KnownOrNull(snapshot.High),
            Low: KnownOrNull(snapshot.Low));
    }

    private static decimal? KnownOrNull(decimal value) => value > 0m ? value : null;
}
