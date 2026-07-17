namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, ADR-0004, IADR-0022/0064: 情報源の構成（Collection:Source）。すべて既定は空＝当該ソースを有効化しない
// （安全既定＝外部接続しない）。Provider に列挙されたソースのみ、必須構成が揃っていれば実接続する。
internal sealed class CollectionSourceOptions
{
    public const string SectionName = "Collection:Source";

    // 有効化する情報源。カンマ区切りで複数指定できる（例: finnhub,sec-edgar,fred）。未設定/none は収集しない。
    public string? Provider { get; set; }

    public FinnhubOptions Finnhub { get; set; } = new();

    public SecEdgarOptions SecEdgar { get; set; } = new();

    public EdinetOptions Edinet { get; set; } = new();

    public BojOptions Boj { get; set; } = new();

    public FredOptions Fred { get; set; } = new();
}

// Finnhub Free（米国株のライブ市況・60回/分）。
internal sealed class FinnhubOptions
{
    public string? ApiKey { get; set; }

    public string[] Symbols { get; set; } = [];
}

// SEC EDGAR（米国の開示・キー不要）。UserAgent は SEC 規約で必須の連絡先（例: "AiStockTrading/1.0 (owner@example.com)"）。
internal sealed class SecEdgarOptions
{
    public string? UserAgent { get; set; }

    public string[] Ciks { get; set; } = [];
}

// EDINET API v2（日本の開示・要 API キー）。
internal sealed class EdinetOptions
{
    public string? SubscriptionKey { get; set; }
}

// 日銀 時系列統計データ API（キー不要）。Db は統計の分類（例: CO）、SeriesCodes は取得する系列コード。
internal sealed class BojOptions
{
    public string? Db { get; set; }

    public string[] SeriesCodes { get; set; } = [];
}

// FRED（米マクロ・要 API キー）。SeriesIds は取得する系列 ID（例: DGS10・UNRATE）。
internal sealed class FredOptions
{
    public string? ApiKey { get; set; }

    public string[] SeriesIds { get; set; } = [];
}
