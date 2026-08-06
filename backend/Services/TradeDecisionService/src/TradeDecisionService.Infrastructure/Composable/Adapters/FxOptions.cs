namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, FR-17, #257, #364, IADR-0107/0152: 為替レート源の構成（セクション "Fx"）。既定は no-op＝外部へ接続しない。
// レートが解決できない間、非基準通貨（日本株）の新規建ては見送られる（基準通貨の米国株は影響を受けない）。
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
    /// 採用してよい観測の古さの上限（日）の既定値。#271, IADR-0112 決定1: データ源の**公表周期**から導く。
    /// `DEXJPUS` は系列こそ営業日次だが、公表は H.10 週次リリース（月曜 16:15 ET・前週金曜まで一括収載／
    /// 月曜が祝日なら火曜）であり、最新観測の齢は「公表間隔 7 日 ＋ 公表ラグ（金→月）3 日 ＋ 祝日ずれ 2 日
    /// ＋ 公表時刻」＝最大 12.84 日まで積み上がる。旧既定 7 日は予定どおりの公表でも毎週必ず超過していた。
    /// </summary>
    public const int DefaultMaxRateAgeDays = 14;

    /// <summary>
    /// 構成で指定できる鮮度上限の上限（日）。#271, IADR-0112 決定2: 週次公表が 4 回以上連続で落ちる事態は
    /// 公表周期では説明できない。「動かないので 365 にする」といった運用で鮮度 guard を実質無効化させないため、
    /// 設定値ではなく構造で担保する（IADR-0059 の保持期間**下限**クランプと対称）。
    /// </summary>
    public const int MaxAllowedRateAgeDays = 31;

    /// <summary>
    /// 採用してよい観測の古さの上限（日）。既定 14 日（<see cref="DefaultMaxRateAgeDays"/>）。週末・連休・週次公表の
    /// 間隔で 10 日以上空くのは正常だが、これを超えた観測は「レート無し」として扱い、古いレートでの発注を止める
    /// （IADR-0107 決定5）。0 以下は既定へ、<see cref="MaxAllowedRateAgeDays"/> 超はその値へ丸める。
    /// </summary>
    public int MaxRateAgeDays { get; set; } = DefaultMaxRateAgeDays;

    /// <summary>FRED（Provider="fred"）の構成。API キーが空なら no-op へ倒す（実接続しない）。</summary>
    public FredFxOptions Fred { get; set; } = new();
}

// #257, IADR-0064/0106: FRED の FX 系列構成。情報収集の Collection:Source:Fred とは別枠にする（有効化の判断も
// レート予算も別）。同じ FRED アカウントの鍵を両方へ設定するのは運用上は自由。
internal sealed class FredFxOptions
{
    /// <summary>API キー（既定は空＝no-op へフォールバック）。環境変数から与え、appsettings に実値を置かない。</summary>
    public string? ApiKey { get; set; }

    /// <summary>USD/JPY の系列 ID。既定 DEXJPUS（円/ドル・営業日次）。JPY のレートは逆数で得る（IADR-0152 決定2）。</summary>
    public string SeriesId { get; set; } = FredFxRateSource.DefaultSeriesId;

    /// <summary>API のベース URL。既定で足りるため通常は設定不要（テスト・将来の移行用）。</summary>
    public string BaseUrl { get; set; } = FredFxRateSource.DefaultBaseUrl;

    /// <summary>当サービスに配るレート予算（回/分）。既定 5（公表上限 120回/分に対し十分小さい）。</summary>
    public int RequestsPerMinute { get; set; } = 5;
}
