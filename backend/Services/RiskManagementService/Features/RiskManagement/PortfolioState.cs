using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, IADR-0005, IADR-0008: ポートフォリオの生の運用状態。kill switch を除く数値状態を保持する。
// これに kill switch 状態を合成して判定入力 PortfolioSnapshot（Domain）を組み立てる（PortfolioSnapshotBuilder）。
public record PortfolioState
{
    /// <summary>
    /// 台帳由来の現在エクイティ（初期資金 ＋ 累計実現損益 ＋ 含み損益。基準通貨 USD）。
    /// <para>
    /// 🔴 <b>統制上限の基準資金（equity）ではない。</b> #869 / ADR-0041 決定2 により、基準資金の供給元は
    /// **ブローカーの口座照会**（<c>ICapitalBaselineStore</c>）に確定した。本値はドローダウン
    /// （<c>PortfolioValuation.DrawdownRatio</c>・ピークとの比。IADR-0066）の入力**専用**である。
    /// </para>
    /// <para>
    /// 名前を <c>Capital</c> から変えてあるのは、<b>台帳から基準資金を導く経路を型の上から消すため</b>である
    /// （旧 <c>Capital</c>＝初期資金 ＋ 当日より前の実現損益は、含み損益を含まない点で計画の定義と食い違っていた）。
    /// </para>
    /// </summary>
    public required decimal LedgerEquity { get; init; }

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
