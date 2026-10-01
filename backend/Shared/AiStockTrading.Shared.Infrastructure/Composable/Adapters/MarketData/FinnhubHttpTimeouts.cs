namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-02, FR-01, FR-03, FR-10, FR-16, #1133, IADR-0469: Finnhub を呼ぶ名前付き HttpClient の打ち切り（HttpClient.Timeout）の
// 唯一の置き場所。既定の 100 秒のままだと、TLS ハンドシェイクが返らない相手（2026-09-30 夜に 3 回）に 1 要求あたり最大 100 秒待つ。
//
// - Quote: 現在値 1 件（/quote）の照会。取引判断の価格文脈・市場監視の巡回（既定 60 秒）・リスク管理の補充（既定 60 秒）・
//   報告書のドラフトが使う "marketdata" クライアント。平常の応答は 1 秒未満であり、5 秒を超えたら「取得できない」へ倒す
//   （FinnhubMarketDataSource が打ち切りを null に写す。呼び出し側の停止要求だけは OperationCanceledException のまま伝える）。
// - Collection: 情報収集の "collection" クライアント（Finnhub の /quote・企業ニュースと、同じクライアントを使う他の情報源）。
//   巡回は 30 分間隔で直列に走るため 1 要求の上限は Quote より長く取るが、有界にする（応答本文の読み込みも含む）。
//   打ち切りは SourceFetchRunner がソース単位の欠測に写す（従来の通信失敗と同じ経路）。
public static class FinnhubHttpTimeouts
{
    /// <summary>"marketdata"（Finnhub /quote の現在値照会）の上限。</summary>
    public static readonly TimeSpan Quote = TimeSpan.FromSeconds(5);

    /// <summary>"collection"（情報収集の情報源。Finnhub の /quote・企業ニュースを含む）の上限。</summary>
    public static readonly TimeSpan Collection = TimeSpan.FromSeconds(15);
}
