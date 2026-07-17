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
}
