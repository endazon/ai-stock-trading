namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, ADR-0004, ADR-0020, IADR-0022/0064: 情報源の構成（Collection:Source）。すべて既定は空＝当該ソースを有効化しない
// （安全既定＝外部接続しない）。Provider に列挙されたソースのみ、必須構成が揃っていれば実接続する。
public sealed class CollectionSourceOptions
{
    public const string SectionName = "Collection:Source";

    // 有効化する情報源。カンマ区切りで複数指定できる（例: finnhub,finnhub-news,google-news,sec-edgar,fred）。
    // 未設定/none は収集しない。名前は InformationSourceCatalog の見出しと一致させる。
    public string? Provider { get; set; }

    // ADR-0005 決定5 / ADR-0020 決定5: 有料化（または無料枠の実質使用不能な縮小）に伴う**推奨への一時降格**。
    // カンマ区切りのソース名。降格中は欠測時の扱いが「記録のみ」になる（必須のまま放置して取引を止めない）。
    public string? DemotedToRecommended { get; set; }

    public FinnhubOptions Finnhub { get; set; } = new();

    public GoogleNewsOptions GoogleNews { get; set; } = new();

    public SecEdgarOptions SecEdgar { get; set; } = new();

    public EdinetOptions Edinet { get; set; } = new();

    public BojOptions Boj { get; set; } = new();

    public FredOptions Fred { get; set; } = new();
}

// Finnhub Free（米国株のライブ市況・企業ニュース）。
//
// IADR-0275: 実クラスタでの実測により、**分次の実効上限は公称どおり 60 回/60 秒の固定ウィンドウ**である
// ことを確認した（ローリングではない。ウィンドウ境界で満額へ完全リセットされる）。既定値 30 は公称値の
// 1/2（保守側）のまま据え置く。**日次上限は依然として未実測**（ADR-0020 §結果 のフォローアップ。実測
// セッションでは持続的なブロックを観測できなかったが、確定にはより長時間の観察が要る）。
// DailyRequestLimit は **null＝未実測**を既定とする。**推測値を実測として焼き込まない。**
public sealed class FinnhubOptions
{
    public string? ApiKey { get; set; }

    public string[] Symbols { get; set; } = [];

    /// <summary>送信前に自制する 1 分あたりの要求数（既定 30 ＝ 実測確認済み上限 60 回/60 秒の 1/2。IADR-0275）。</summary>
    public int RateLimitPerMinute { get; set; } = 30;

    /// <summary>
    /// 1 日あたりの要求上限。<b>null＝未実測</b>（IADR-0275 の実測セッションでも確定できず、継続観察が要る）。
    /// 設定されたときだけ監視銘柄数の上限を逆算する（FinnhubQuotaCalculator）。
    /// </summary>
    public int? DailyRequestLimit { get; set; }

    /// <summary>企業ニュースの取得対象期間（日数）。既定は前日から当日まで。</summary>
    public int NewsLookbackDays { get; set; } = 1;
}

// Google News RSS（キー不要・公式保証なし）。高頻度ポーリングはブロックされ得るため既定は 1 分 1 回。
public sealed class GoogleNewsOptions
{
    /// <summary>検索クエリ（銘柄名・キーワード）。空＝このソースを有効化しない。</summary>
    public string[] Queries { get; set; } = [];

    public int RateLimitPerMinute { get; set; } = 1;

    public int MaxItemsPerQuery { get; set; } = 20;
}

// SEC EDGAR（米国の開示・キー不要）。UserAgent は SEC 規約で必須の連絡先（例: "AiStockTrading/1.0 (owner@example.com)"）。
public sealed class SecEdgarOptions
{
    public string? UserAgent { get; set; }

    public string[] Ciks { get; set; } = [];

    /// <summary>送信前に自制する 1 秒あたりの要求数（既定 5 ＝ 公表 10 回/秒/IP の 1/2）。</summary>
    public int RateLimitPerSecond { get; set; } = 5;
}

// EDINET API v2（日本の開示・要 API キー）。レート制限は非公表のため 1 分 1 回程度に自制する。
public sealed class EdinetOptions
{
    public string? SubscriptionKey { get; set; }

    public int RateLimitPerMinute { get; set; } = 1;
}

// 日銀 時系列統計データ API（キー不要）。Db は統計の分類（例: CO）、SeriesCodes は取得する系列コード。
// 「短時間における連続したアクセスは禁止」のため、系列を 1 要求に束ねたうえで 1 分 1 回に自制する。
public sealed class BojOptions
{
    public string? Db { get; set; }

    public string[] SeriesCodes { get; set; } = [];

    public int RateLimitPerMinute { get; set; } = 1;
}

// FRED（米マクロ・要 API キー・公表 120 回/分）。既定はその 1/2。
public sealed class FredOptions
{
    public string? ApiKey { get; set; }

    public string[] SeriesIds { get; set; } = [];

    public int RateLimitPerMinute { get; set; } = 60;
}
