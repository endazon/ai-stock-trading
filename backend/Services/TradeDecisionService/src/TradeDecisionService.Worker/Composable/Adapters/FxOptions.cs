namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// FR-10, FR-17, #257, IADR-0106: 為替レート源の構成（セクション "Fx"）。既定は no-op＝外部へ接続しない。
// レートが解決できない間、非基準通貨（米国株）の新規建ては見送られる（基準通貨の日本株は影響を受けない）。
internal sealed class FxOptions
{
    public const string SectionName = "Fx";

    /// <summary>
    /// レート源の選択。既定・空・"none"・未知の値・キー無しはすべて no-op（実接続しない）へ倒す。
    /// 現在の実装は "fred"（計画 05_trading-assumptions §3 の「日銀API または FRED」のうち FRED）。
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// 取得済みレートの再利用時間（秒）。既定 21,600 秒（6 時間）。DEXJPUS は営業日次系列で日中に更新されないため、
    /// 判断サイクルごとに叩く必要がない。
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 21_600;

    /// <summary>
    /// 採用してよい観測の古さの上限（日）。既定 7 日。週末・連休で数日空くのは正常だが、これを超えた観測は
    /// 「レート無し」として扱い、古いレートでの発注を止める（IADR-0106 決定5）。0 以下は既定 7 日へ丸める。
    /// </summary>
    public int MaxRateAgeDays { get; set; } = 7;

    /// <summary>FRED（Provider="fred"）の構成。API キーが空なら no-op へ倒す（実接続しない）。</summary>
    public FredFxOptions Fred { get; set; } = new();
}

// #257, IADR-0064/0106: FRED の FX 系列構成。情報収集の Collection:Source:Fred とは別枠にする（有効化の判断も
// レート予算も別）。同じ FRED アカウントの鍵を両方へ設定するのは運用上は自由。
internal sealed class FredFxOptions
{
    /// <summary>API キー（既定は空＝no-op へフォールバック）。環境変数から与え、appsettings に実値を置かない。</summary>
    public string? ApiKey { get; set; }

    /// <summary>USD/JPY の系列 ID。既定 DEXJPUS（円/ドル・営業日次）。</summary>
    public string SeriesId { get; set; } = FredFxRateSource.DefaultSeriesId;

    /// <summary>API のベース URL。既定で足りるため通常は設定不要（テスト・将来の移行用）。</summary>
    public string BaseUrl { get; set; } = FredFxRateSource.DefaultBaseUrl;

    /// <summary>当サービスに配るレート予算（回/分）。既定 5（公表上限 120回/分に対し十分小さい）。</summary>
    public int RequestsPerMinute { get; set; } = 5;
}
