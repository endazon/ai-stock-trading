using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10: 判定時点のポートフォリオ・運用状態のスナップショット
public record PortfolioSnapshot
{
    /// <summary>
    /// 当日開始時点の運用資金（基準通貨・円）。日次損失上限・1取引リスクの判定基準に用いる。
    /// 当日の実現損益で目減りしない固定基準として扱い（当日中は不変）、しきい値が損失で
    /// 自己参照的に縮小しないようにする。実現損益は <see cref="DailyRealizedPnl"/> で別途保持する。
    /// </summary>
    public required decimal Capital { get; init; }

    public int OpenPositionCount { get; init; }

    /// <summary>
    /// 保有ポジションの投入中資金＝取得額合計（コストベース、基準通貨・円）。段階資金上限の累計判定に用いる
    /// （Issue #27）。時価ではなく取得額を基準とする理由は IADR-0005 を参照。
    /// </summary>
    public decimal InvestedCapital { get; init; }

    /// <summary>当日の発注金額累計（基準通貨・円）。</summary>
    public decimal DailyOrderedAmount { get; init; }

    /// <summary>当日の実現損益（負値 = 損失）。</summary>
    public decimal DailyRealizedPnl { get; init; }

    /// <summary>資金ピークからのドローダウン率（0.10 = 10%）。</summary>
    public decimal DrawdownRatio { get; init; }

    public int ConsecutiveLosses { get; init; }

    /// <summary>
    /// 当日に売買が成立した銘柄を（銘柄コード, 市場）で保持する（差金決済防止の判定に使用）。
    /// 禁止銘柄判定と対称に市場込みで照合し、別市場の同一コードの誤拒否を防ぐ（Issue #26）。
    /// </summary>
    public IReadOnlySet<(string Symbol, Market Market)> SymbolsTradedToday { get; init; }
        = new HashSet<(string, Market)>();

    /// <summary>全停止スイッチ（kill switch）。利用者のみ操作できる。</summary>
    public bool KillSwitchEngaged { get; init; }
}
