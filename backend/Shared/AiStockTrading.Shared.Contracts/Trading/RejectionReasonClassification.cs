namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-20, ADR-0016 決定10: 発注拒否の分類。段階ゲート（Stage 1→2）の合格条件
// 「統制違反 0 件」が数える対象を**クラス C 限定**にするための単一情報源である
// （project-planning#58 の裁定・06_daytrading-review §4.1）。
public enum RejectionReasonClass
{
    /// <summary>
    /// クラス A: 統制が**設計どおり作動した**記録（金額上限超過・損失上限到達・空売り統制の作動など）。
    /// 数えるとゲートが恒久ブロックになるため「統制違反 0 件」には計上しない。
    /// </summary>
    A,

    /// <summary>
    /// クラス B: 緊急停止中・段階制約・ガード設定による拒否。取引を止めている状態そのものの記録であり、
    /// クラス A と同じく「統制違反 0 件」には計上しない。
    /// </summary>
    B,

    /// <summary>
    /// クラス C: **AI が法令・計画上の禁止事項を犯そうとした**件数（禁止銘柄・相場操縦パターン）。
    /// 「本来ゼロであるべきで、かつ実際にゼロになり得る」唯一の区分であり、段階昇格ゲートが数える対象。
    /// </summary>
    C,
}

/// <summary>
/// FR-10, FR-20, ADR-0016 決定10: 拒否理由のクラス分類。
/// <para>
/// **空売りの拒否理由 9 種はすべてクラス A** であり、クラス C（<see cref="RejectionReason.BannedSymbol"/> /
/// <see cref="RejectionReason.ManipulativeOrderPattern"/>）に混ぜてはならない。市況由来の事象を
/// 「AI が禁止事項を犯そうとした件数」に混入させると、段階昇格ゲートが機能しなくなる。
/// </para>
/// </summary>
public static class RejectionReasonClassification
{
    /// <summary>拒否理由のクラスを返す。</summary>
    public static RejectionReasonClass ClassOf(RejectionReason reason) => reason switch
    {
        // クラス C: 禁止事項への抵触（限定列挙。project-planning#58 の裁定）。
        RejectionReason.BannedSymbol or RejectionReason.ManipulativeOrderPattern => RejectionReasonClass.C,

        // クラス B: 緊急停止中・段階制約・ガード設定による拒否。
        RejectionReason.KillSwitchActive
            or RejectionReason.TradingPaused
            or RejectionReason.StageProhibitsLiveTrading
            or RejectionReason.StageCapitalCapExceeded
            or RejectionReason.ProductTypeDisabled
            or RejectionReason.MarketDisabled => RejectionReasonClass.B,

        // クラス A: 統制の正常作動（金額・件数・損失の上限、差金決済防止、空売り統制の 8 規則＝拒否理由 9 種）。
        _ => RejectionReasonClass.A,
    };

    /// <summary>
    /// 段階ゲートの「統制違反」に計上する理由か（＝クラス C か）。FR-20・06_daytrading-review §4.1。
    /// </summary>
    public static bool CountsAsControlViolation(RejectionReason reason) =>
        ClassOf(reason) == RejectionReasonClass.C;

    /// <summary>
    /// 1 回の発注拒否を「統制違反 1 件」として数えるか。**計上単位は 1 回の発注拒否につき 1 件**であり、
    /// 1 回の拒否で複数理由が返っても 1 件である（06_daytrading-review §4.1）。
    /// クラス C の理由を 1 つでも含めば計上する。
    /// </summary>
    public static bool CountsAsControlViolation(IEnumerable<RejectionReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return reasons.Any(CountsAsControlViolation);
    }
}
