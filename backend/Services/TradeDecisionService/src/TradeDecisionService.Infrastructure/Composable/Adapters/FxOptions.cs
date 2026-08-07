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
    public const int DefaultMaxRateAgeDays = 30;

    /// <summary>
    /// **警告**を出す観測の古さのしきい値（日）の既定値。**5 日**（ADR-0022 決定4・2026-08-07 改訂。起案時は 3 日）。
    /// <para>
    /// FR-10, FR-17, #381, IADR-0174: 計画 §5 の縮退は 3 段である —— <b>5 日以下＝通常運用 ／ 5 日超〜30 日以下＝
    /// 直近レートで続行し警告 ／ 30 日超＝新規建てを停止</b>（手仕舞い・損切りは止めない）。
    /// </para>
    /// <para>
    /// <b>警告と停止は役割が違う</b> —— 前者は<b>気づくため</b>、後者は<b>統制が意味を失った状態で発注しないため</b>である。
    /// 旧実装は上限だけを持っており、この 2 つが同じ値に潰れていた（超えたらいきなり停止）。
    /// </para>
    /// <para>
    /// 3 日から 5 日へ改まった理由: 日銀の検索サイトへの収録は<b>翌々営業日 8:50 頃</b>であり、
    /// <b>平常時でも月曜・火曜は最新データが 4 日前</b>になって 3 日を必ず超える（ADR-0022 決定4 の 2026-08-06 追補）。
    /// 5 日なら月・火の 4 日を平常として吸収し、3 連休明けの火曜（5 日）まで収まる。
    /// </para>
    /// </summary>
    public const int DefaultStaleRateWarningDays = 5;

    /// <summary>
    /// 構成で指定できる鮮度上限の上限（日）。#271, IADR-0112 決定2: 週次公表が 4 回以上連続で落ちる事態は
    /// 公表周期では説明できない。「動かないので 365 にする」といった運用で鮮度 guard を実質無効化させないため、
    /// 設定値ではなく構造で担保する（IADR-0059 の保持期間**下限**クランプと対称）。
    /// <para>
    /// **#381, IADR-0174 決定2: 31 → 30 へ下げた。** 旧値 31 は<b>計画が「絶対上限」と呼ぶ 30 日を上回っており</b>、
    /// <c>Fx:MaxRateAgeDays: 31</c> と構成すれば<b>計画が新規建てを停止すると定めた 30 日超の観測で発注できた</b>。
    /// クランプは「設定で guard を無効化させない」ための仕組みなのに、<b>その上限自体が計画より緩かった</b>。
    /// 31 は FRED の週次公表から逆算した実装都合の値であり、根拠が計画へ移った今は 30 が正しい。
    /// </para>
    /// </summary>
    public const int MaxAllowedRateAgeDays = 30;

    /// <summary>
    /// 採用してよい観測の古さの上限（日）。既定 30 日（<see cref="DefaultMaxRateAgeDays"/>＝計画 ADR-0022 決定5 の
    /// <b>絶対上限</b>）。これを超えた観測は「レート無し」として扱い、古いレートでの発注を止める（IADR-0107 決定5）。
    /// 0 以下は既定へ、<see cref="MaxAllowedRateAgeDays"/> 超はその値へ丸める。
    /// <para>
    /// **#381, IADR-0174: 14 → 30 へ引き上げた。** 旧値 14 は FRED の週次公表から逆算した実装都合であり
    /// （IADR-0112 決定1）、計画 ADR-0022 が情報源と鮮度を確定したことで根拠が計画側へ移った。
    /// <b>結果として「週次リリース 1 回の丸ごと欠落（17.84 日）を系列側の異常として止める」保護は失われる</b>——
    /// 計画 §5 が「5 日超〜30 日以下＝直近レートで続行し警告」と定めた**計画が選んだ緩和**であり、
    /// 気づくための段は <see cref="StaleRateWarningDays"/> が受け持つ。
    /// </para>
    /// </summary>
    public int MaxRateAgeDays { get; set; } = DefaultMaxRateAgeDays;

    /// <summary>
    /// 警告を出す観測の古さのしきい値（日）。既定 5 日（<see cref="DefaultStaleRateWarningDays"/>）。
    /// 0 以下は既定へ、<see cref="MaxRateAgeDays"/> 超はその値へ丸める（#381・IADR-0174 決定3）。
    /// <b>警告が上限を超えると、警告が一度も出ないまま停止する</b>——気づく機会そのものが消えるため、
    /// 上限でクランプする。
    /// </summary>
    public int StaleRateWarningDays { get; set; } = DefaultStaleRateWarningDays;

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
