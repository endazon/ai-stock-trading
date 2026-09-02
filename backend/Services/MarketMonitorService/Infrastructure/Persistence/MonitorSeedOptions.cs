using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-02, FR-13, #286, IADR-0281: watchlist の初回シード（構成ベース。利用者裁定 2026-09-02・案(b)）。
// TradeDecisionService の ConfigurationWatchlistProvider.WatchlistEntry（TradeCycle:Watchlist）と対称の
// 構成形式（列挙は列挙名でバインド）。既定は空リスト＝構成未投入の環境（本番 values.yaml 含む）は
// MonitorDefaults.CreateSettings() が従来どおり空でシードする（現行挙動のバイト等価）。
public sealed class MonitorSeedOptions
{
    // MonitorOptions（Hosted・PollIntervalSeconds）と同じ節 "Monitor" を共有する。プロパティ名が異なるため
    // 双方の Get<T>() が互いの値を無視して衝突しない。
    public const string SectionName = "Monitor";

    public IReadOnlyList<SeedSymbolEntry> SeedSymbols { get; init; } = [];

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
