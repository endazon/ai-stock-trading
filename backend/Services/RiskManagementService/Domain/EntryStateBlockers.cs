using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// 🔴 FR-10, FR-04, ADR-0003, #1113, IADR-0463 決定 2: 新規建てを**状態だけで確定的に**拒否する述語。
//
// 審査（RiskEvaluator.Evaluate）と、判断が LLM を呼ぶ前に読む「新規建ての可否」の口（EntryBlockersService）が
// **同じ関数を呼ぶ**。規則を 2 か所に置かない（IADR-0394 が案 B を退けた理由そのもの）。述語を直すときはここだけを直す。
//
// 対象は「新規建てだけを拒否し、注文の数量・価格・商品種別に依存せず、状態が既知」の 7 理由である
// （母集合と除外の理由は作業仕様書 20260930_1113_entry-blockers-before-llm）。
// 🔴 **不明は返さない**（裁定 3）。StopOutStatusUnknown・資金の未供給・口座種別の未確認・縮退の不明・GFV 件数の未供給は
// 審査では止まるが、口は「確定した」とは答えない —— 判断側は LLM を呼ぶ側へ倒れる（審査が止める）。
public static class EntryStateBlockers
{
    /// <summary>口が返し得る理由（審査の到達順）。<c>DailyLossLimitReached</c> はロックアウトでも立つ。</summary>
    public static IReadOnlyList<RejectionReason> Determinable { get; } =
    [
        RejectionReason.KillSwitchActive,
        RejectionReason.TradingPaused,
        RejectionReason.StoppedOutSameDay,
        RejectionReason.GoodFaithViolationLimitReached,
        RejectionReason.MaxPositionsExceeded,
        RejectionReason.DailyLossLimitReached,
        RejectionReason.MaxDrawdownReached,
    ];

    /// <summary>全停止スイッチ（kill switch）。</summary>
    public static bool KillSwitch(PortfolioSnapshot snapshot) => snapshot.KillSwitchEngaged;

    /// <summary>取引の一時停止（pause）。</summary>
    public static bool Paused(PortfolioSnapshot snapshot) => snapshot.TradingPaused;

    /// <summary>
    /// 当日の損切り（IADR-0394）。新規建て（<paramref name="entrySide"/>）と同じ方向の建玉の状態を見る。
    /// 損切り済みなら <c>StoppedOutSameDay</c>、不明なら <c>StopOutStatusUnknown</c>、無しなら null。
    /// </summary>
    public static RejectionReason? StopOut(StopOutReentrySupply stopOuts, TradeSide entrySide) =>
        stopOuts.ForEntry(entrySide) switch
        {
            StopOutStatus.StoppedOut => RejectionReason.StoppedOutSameDay,
            StopOutStatus.Unknown => RejectionReason.StopOutStatusUnknown,
            _ => null,
        };

    /// <summary>保有建玉数の上限（ADR-0016 決定 9。未約定の新規建てを含めて数える＝IADR-0346）。</summary>
    public static bool MaxPositions(RiskManagementSettings settings, PortfolioSnapshot snapshot) =>
        snapshot.OpenPositionCount >= settings.Limits.MaxOpenPositions;

    /// <summary>
    /// 日次損失上限（実現＋含み損。IADR-0008）。equity が未供給なら判定しない（審査は CapitalBaselineUnavailable で止める）。
    /// </summary>
    public static bool DailyLoss(RiskManagementSettings settings, PortfolioSnapshot snapshot) =>
        snapshot.Capital is { } equity
        && snapshot.DailyRealizedPnl + snapshot.UnrealizedPnl <= -(equity * settings.Limits.DailyLossLimitRatio);

    /// <summary>最大ドローダウン。</summary>
    public static bool MaxDrawdown(RiskManagementSettings settings, PortfolioSnapshot snapshot) =>
        snapshot.DrawdownRatio >= settings.Limits.MaxDrawdownRatio;

    /// <summary>
    /// 状態から**確定する**新規建ての拒否理由（審査と同じ述語・同じ並び）。<paramref name="lockedOut"/> は当日の日次損失の
    /// ロックアウト（審査のサービスが保持する。<c>OrderScreeningService.IsLockoutActive</c>）。
    /// </summary>
    public static IReadOnlyList<RejectionReason> Determine(
        TradeSide entrySide,
        RiskManagementSettings settings,
        PortfolioSnapshot snapshot,
        StopOutReentrySupply stopOuts,
        bool lockedOut)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stopOuts);

        var reasons = new List<RejectionReason>();
        if (KillSwitch(snapshot))
            reasons.Add(RejectionReason.KillSwitchActive);

        if (Paused(snapshot))
            reasons.Add(RejectionReason.TradingPaused);

        // 🔴 不明（StopOutStatusUnknown）は返さない。
        if (StopOut(stopOuts, entrySide) == RejectionReason.StoppedOutSameDay)
            reasons.Add(RejectionReason.StoppedOutSameDay);

        // 🔴 GFV は**件数が既知**のときだけ（審査は未供給〔null〕でも止めるが、それは不明である）。
        // 口座種別は審査と同じく照会結果で見る（現金口座でのみ加わる統制）。
        if (snapshot.Account?.AccountType == AccountType.Cash
            && snapshot.GoodFaithViolations is not null
            && AccountTypePolicy.BlocksForGoodFaithViolations(snapshot.GoodFaithViolations))
        {
            reasons.Add(RejectionReason.GoodFaithViolationLimitReached);
        }

        if (MaxPositions(settings, snapshot))
            reasons.Add(RejectionReason.MaxPositionsExceeded);

        // 審査はロックアウト中なら判定コアが到達と見なくても同じ理由で止める（OrderScreeningService）。
        if (DailyLoss(settings, snapshot) || lockedOut)
            reasons.Add(RejectionReason.DailyLossLimitReached);

        if (MaxDrawdown(settings, snapshot))
            reasons.Add(RejectionReason.MaxDrawdownReached);

        return reasons;
    }
}
