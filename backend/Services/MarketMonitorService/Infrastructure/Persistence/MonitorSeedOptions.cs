using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-02, FR-13, #286, IADR-0282: watchlist の初回シード（構成ベース。利用者裁定 2026-09-02・案(b)）。
// TradeDecisionService の ConfigurationWatchlistProvider.WatchlistEntry（TradeCycle:Watchlist）と対称の
// 構成形式（列挙は列挙名でバインド）。既定は空リスト＝構成未投入の環境（本番 values.yaml 含む）は
// MonitorDefaults.CreateSettings() が従来どおり空でシードする（現行挙動のバイト等価）。
public sealed class MonitorSeedOptions
{
    // MonitorOptions（Hosted・PollIntervalSeconds）と同じ節 "Monitor" を共有する。プロパティ名が異なるため
    // 双方の Get<T>() が互いの値を無視して衝突しない。
    public const string SectionName = "Monitor";

    public IReadOnlyList<SeedSymbolEntry> SeedSymbols { get; init; } = [];

    /// <summary>
    /// FR-02, FR-13, #1065 F1: 未定義の市場の構成値（例 <c>Monitor:SeedSymbols:0:Market=7</c>。列挙名でない番号は構成の束縛が
    /// そのまま <c>(Market)7</c> として通す）を持つ要素の説明。無ければ空。起動時の検証（Program.cs の ValidateOnStart）が使う。
    /// </summary>
    public IReadOnlyList<string> UndefinedMarkets() =>
        [.. SeedSymbols
            .Select((e, i) => (e, i))
            .Where(x => !Enum.IsDefined(x.e.Market))
            .Select(x => $"{SectionName}:SeedSymbols:{x.i}（{x.e.Symbol}・市場 {(int)x.e.Market}）")];

    public IReadOnlyCollection<MonitoredSymbol> ToMonitoredSymbols() =>
        [.. SeedSymbols
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => new MonitoredSymbol(e.Symbol!.Trim(), e.Market))];

    // 構成バインド用（Market は列挙名でバインドされる）。
    public sealed class SeedSymbolEntry
    {
        public string? Symbol { get; set; }

        public Market Market { get; set; }
    }
}
