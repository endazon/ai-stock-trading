namespace AiStockTrading.RiskManagement.Domain;

// FR-10: 判定時点のポートフォリオ・運用状態のスナップショット
public record PortfolioSnapshot
{
    /// <summary>運用資金（基準通貨・円）。</summary>
    public required decimal Capital { get; init; }

    public int OpenPositionCount { get; init; }

    /// <summary>当日の発注金額累計（基準通貨・円）。</summary>
    public decimal DailyOrderedAmount { get; init; }

    /// <summary>当日の実現損益（負値 = 損失）。</summary>
    public decimal DailyRealizedPnl { get; init; }

    /// <summary>資金ピークからのドローダウン率（0.10 = 10%）。</summary>
    public decimal DrawdownRatio { get; init; }

    public int ConsecutiveLosses { get; init; }

    /// <summary>当日に売買が成立した銘柄（差金決済防止の判定に使用）。</summary>
    public IReadOnlySet<string> SymbolsTradedToday { get; init; } = new HashSet<string>();

    /// <summary>全停止スイッチ（kill switch）。利用者のみ操作できる。</summary>
    public bool KillSwitchEngaged { get; init; }
}
