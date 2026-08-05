using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10: 判定時点のポートフォリオ・運用状態のスナップショット
public record PortfolioSnapshot
{
    /// <summary>
    /// 判定に用いる自己資金（**equity**）。当日開始時点の運用資金（基準通貨・USD）であり、
    /// <b>前営業日終値時点の評価額</b>と同義である（計画 05_trading-assumptions §5 注記）。
    /// <para>
    /// FR-10, #329, IADR-0130 決定2: 日次損失上限・1 取引リスク・最大 DD に加え、
    /// **金額系の統制上限 3 値（1 注文 25% / 1 日 150% / 保有建玉数）もこの値を基準に解決する**。
    /// 当日の実現損益で目減りしない固定基準として扱い（当日中は不変）、しきい値が損失で
    /// 自己参照的に縮小しないようにする。実現損益は <see cref="DailyRealizedPnl"/> で別途保持する。
    /// </para>
    /// <para>
    /// 日中の評価損益を含めないのは、含み益で上限が緩み含み損で締まるという**逆方向の作用**を避けるため
    /// （計画 §5 注記。米国の日計り買付余力が前営業日終値時点で固定されるのと同じ考え方）。
    /// </para>
    /// </summary>
    public required decimal Capital { get; init; }

    /// <summary>保有**建玉**数（ADR-0016 決定9。「保有銘柄数」では数えない）。</summary>
    public int OpenPositionCount { get; init; }

    /// <summary>
    /// 保有ポジションの投入中資金＝取得額合計（コストベース、基準通貨・USD）。段階資金上限の累計判定に用いる
    /// （Issue #27）。時価ではなく取得額を基準とする理由は IADR-0005 を参照。
    /// </summary>
    public decimal InvestedCapital { get; init; }

    /// <summary>
    /// 当日の発注金額累計（基準通貨・USD）。**新規建て（<c>PositionEffect.Open</c>）の約定のみ**を積む
    /// （計画 §5「新規建ての発注代金の合計で判定し、手仕舞い〔決済〕注文は算入しない」・#302 の裁定・
    /// IADR-0130 決定4）。集計は <c>PortfolioProjection</c> の責務。
    /// </summary>
    public decimal DailyOrderedAmount { get; init; }

    /// <summary>当日の実現損益（負値 = 損失）。</summary>
    public decimal DailyRealizedPnl { get; init; }

    /// <summary>
    /// 保有ポジションの含み損益＝評価損益（負値 = 含み損、基準通貨・USD）。日次損失上限は実現損益と含み損益の
    /// 合算で判定する（IADR-0008, Issue #31）。含み損の大きいポジションを抱えたまま実現ゼロで検知が遅れる穴を塞ぐ。
    /// 評価損益は日次終値（全体前提条件 §5 の為替評価方法）で算出する想定。集計はリスク管理ホスト（#12）の責務。
    /// </summary>
    public decimal UnrealizedPnl { get; init; }

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

    /// <summary>
    /// 取引の一時停止（pause）。利用者が手動で発動し、期限を持たず `/resume` まで継続する軽い統制（ADR-0009）。
    /// kill switch と同じく新規建て（エントリー）のみを止め、手仕舞い（Close）・損切りは止めない。
    /// 日次損失ロックアウト（IADR-0008）とは別状態で、`/resume` は本状態のみを解除する。
    /// </summary>
    public bool TradingPaused { get; init; }
}
