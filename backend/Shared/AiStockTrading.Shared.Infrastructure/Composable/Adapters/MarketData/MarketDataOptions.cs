namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-10, FR-16, #81, IADR-0066: 現在値（時価評価）の構成。構成セクションはサービスごとの appsettings の "MarketData"。
// 保持期限は現在値を使う全サービスで共通の概念のため共有物に置く。EnableMarkToMarket / RefreshIntervalSeconds は
// リスク管理（判定へ反映するサービス）のみが読む（報告書は評価の提示のみでゲートを持たない）。
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>
    /// 時価評価を有効化する（既定 false＝現行どおり含み 0・DD 0。リスク管理のみ）。
    /// 有効化は DrawdownRatio を初めて非 0 にし、最大DD の取引ゲート（IADR-0008）の判定入力を変えるため、
    /// 実市況の live 検証を経てから人手で切り替える（IADR-0066）。
    /// </summary>
    public bool EnableMarkToMarket { get; set; }

    /// <summary>現在値の補充間隔（秒）。既定 60s（リスク管理のみ）。</summary>
    public int RefreshIntervalSeconds { get; set; } = 60;

    /// <summary>前回値の保持期限（秒）。これを超えた前回値は取得不可として扱い、含みを 0 へ倒す。既定 300s（5 分）。</summary>
    public int MaxQuoteStalenessSeconds { get; set; } = 300;

    /// <summary>
    /// 現在値ソースの選択（#158, IADR-0068 決定 6）。既定・空・"none" は no-op＝実接続しない。
    /// 現在の実装は "finnhub" のみ（未知の値も安全既定の no-op へ倒す）。
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Finnhub（Provider="finnhub"）の構成。API キーが空なら no-op へ倒す（実接続しない）。</summary>
    public FinnhubMarketDataOptions Finnhub { get; set; } = new();
}

// #158, IADR-0068: Finnhub 実市況の構成。情報収集の Collection:Source:Finnhub とは**別枠**にする（有効化の判断も
// レート予算も別。片方の有効化でもう片方が黙って有効になるのは opt-in の粒度として粗い）。同じ Finnhub アカウントの
// 鍵を両方へ設定するのは運用上は自由。
// 🔴 IADR-0275: 実クラスタでの実測により、ローカル実行環境（values-local.yaml）では実際に同一鍵が使われて
// いることを確認した。同一鍵運用では両枠の自制レート合計が実測上限（60/分）を超えないことが必要になる
// （RequestsPerMinute の既定値はこれを踏まえて是正済み）。
public sealed class FinnhubMarketDataOptions
{
    /// <summary>API キー（既定は空＝no-op へフォールバック）。環境変数から与え、appsettings に実値を置かない。</summary>
    public string? ApiKey { get; set; }

    /// <summary>API のベース URL。既定で足りるため通常は設定不要（テスト・将来の移行用）。</summary>
    public string BaseUrl { get; set; } = FinnhubQuoteClient.DefaultBaseUrl;

    /// <summary>
    /// 当サービスに配るレート予算（回/分）。既定 5。プロセスをまたぐ協調は行わないため、全プロセスの合計が
    /// 無料枠（実測 60 回/60 秒固定ウィンドウ。IADR-0275）を超えないよう運用で守る。
    /// IADR-0068 決定 4 は「情報収集 30 ＋ 市況 10 × 3 サービス = 60」としていたが、市況の消費サービスは
    /// 実装上 4 つ（MarketMonitorService/ReportService/RiskManagementService/TradeDecisionService）であり
    /// 過小算定だった。IADR-0275 実測を受け既定を 10→5 に是正（情報収集 30 ＋ 市況 5 × 4 = 50/分。
    /// 情報収集と同一 Finnhub 鍵を共有する運用を前提にした保守値であり、鍵を分ければ引き上げてよい）。
    /// </summary>
    public int RequestsPerMinute { get; set; } = 5;
}
