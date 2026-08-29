using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, IADR-0005, IADR-0008: ポートフォリオの生の運用状態。kill switch を除く数値状態を保持する。
// これに kill switch 状態を合成して判定入力 PortfolioSnapshot（Domain）を組み立てる（PortfolioSnapshotBuilder）。
public record PortfolioState
{
    /// <summary>当日開始時運用資金（固定基準・基準通貨 USD）。日次損失上限・1取引リスクの基準。当日中は不変。</summary>
    public required decimal Capital { get; init; }

    public int OpenPositionCount { get; init; }

    /// <summary>保有ポジションの取得額合計（コストベース・基準通貨 USD）。段階資金上限の累計判定に用いる（IADR-0005）。</summary>
    public decimal InvestedCapital { get; init; }

    /// <summary>当日の発注金額累計（基準通貨 USD）。</summary>
    public decimal DailyOrderedAmount { get; init; }

    /// <summary>当日の実現損益（負値 = 損失）。</summary>
    public decimal DailyRealizedPnl { get; init; }

    /// <summary>含み損益＝評価損益（負値 = 含み損・日次終値評価）。日次損失上限は実現+含みの合算で判定（IADR-0008）。</summary>
    public decimal UnrealizedPnl { get; init; }

    /// <summary>資金ピークからのドローダウン率（0.10 = 10%）。</summary>
    public decimal DrawdownRatio { get; init; }

    public int ConsecutiveLosses { get; init; }

    /// <summary>当日に売買が成立した銘柄（銘柄コード, 市場）。差金決済防止の判定に用いる（#26）。</summary>
    public IReadOnlySet<(string Symbol, Market Market)> SymbolsTradedToday { get; init; }
        = new HashSet<(string, Market)>();
}
