using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-14, IADR-0427 決定 3, IADR-0449 決定 3・4, #753（段 5）: 線上表現 → REST の各アダプタと**同じ射影**（受け手側の写し）。
// 写したあとの解釈（表示の整形・並び替え・欠けた項目の扱い）は REST のアダプタの `internal static` を使う（規則を 2 箇所に書かない）。
//
// 🔴 **原則 A**: 必須の項目の欠落・未指定の段階・読めない数値は、既定値（0・false・Stage 0）で作らず **`null` ＝応答を解釈できない**へ倒す
// （REST の受け手は値型を非 null で受けていたため、欠落を 0 と読む余地があった。gRPC では存在を持つので読み違えない）。
// 🔴 **列挙は名前で写す**（C# の 0 は実在の値＝ Stage 0・内蔵 paper・昇格、proto の 0 は未指定）。表示のラベルは REST と同じ
// 序数（Risk の C# の列挙の値）で引くので、ここで序数へ写す。表示の補助（種別・基準・理由・モード）の未知は -1（表示は「不明(-1)」）。
internal static class NotificationGrpcWire
{
    private const int Unknown = -1;

    // ---- リスク管理: 稼働状態（GET /risk-controls/status と同じ射影） ----

    internal static HttpPauseController.RiskStatusView? ToRiskStatusView(RiskProto.GetRiskStatusResponse r)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (!r.HasKillSwitchEngaged || !r.HasDailyLossLockoutActive || !r.HasTradingPaused || !r.HasNewEntriesBlocked
            || !r.HasOpenPositionCount || !r.HasMaxOpenPositions
            || Stage(r.Stage) is not { } stage
            || Decimal(r.HasDailyRealizedPnl, r.DailyRealizedPnl) is not { } realized
            || Decimal(r.HasUnrealizedPnl, r.UnrealizedPnl) is not { } unrealized
            || Decimal(r.HasDailyPnl, r.DailyPnl) is not { } daily
            || Decimal(r.HasDailyOrderedAmount, r.DailyOrderedAmount) is not { } ordered
            || Decimal(r.HasDrawdownRatio, r.DrawdownRatio) is not { } drawdown
            || Decimal(r.HasMaxDrawdownRatio, r.MaxDrawdownRatio) is not { } maxDrawdown)
            return null;

        // 任意の 2 項目: 欠落は REST の null（解除日が未定・上限を解決できない）。**在るのに読めない**は解釈できないへ倒す（0 と表示しない）。
        DateOnly? releaseOn = null;
        if (r.HasLockoutReleaseOn)
        {
            if (!DateOnly.TryParseExact(r.LockoutReleaseOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return null;
            releaseOn = day;
        }

        decimal? maxDaily = null;
        if (r.HasMaxDailyOrderAmount)
        {
            if (Decimal(true, r.MaxDailyOrderAmount) is not { } value)
                return null;
            maxDaily = value;
        }

        return new HttpPauseController.RiskStatusView(
            r.KillSwitchEngaged, r.DailyLossLockoutActive, releaseOn, r.TradingPaused, r.NewEntriesBlocked, stage,
            realized, unrealized, daily, ordered, maxDaily, drawdown, maxDrawdown, r.OpenPositionCount, r.MaxOpenPositions);
    }

    // ---- リスク管理: 段階ゲートの現況（GET /risk-controls/stage-gate と同じ射影） ----

    internal static HttpStageGateController.StageGateStatusView? ToStageGateView(RiskProto.GetStageGateResponse r)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (Stage(r.CurrentStage) is not { } current
            || r.CurrentSettings is not { } settings || Stage(settings.Stage) is not { } settingsStage
            || Decimal(settings.HasCapitalCapRatio, settings.CapitalCapRatio) is not { } capRatio
            || r.Promotion is not { HasEligible: true } promotion
            || r.Withdrawal is not { HasTriggered: true, HasHaltNewEntries: true } withdrawal)
            return null;

        int? target = null;
        if (promotion.HasTargetStage)
        {
            if (Stage(promotion.TargetStage) is not { } t)
                return null;
            target = t;
        }

        int? proposed = null;
        if (withdrawal.HasProposedStage)
        {
            if (Stage(withdrawal.ProposedStage) is not { } p)
                return null;
            proposed = p;
        }

        var history = new List<HttpStageGateController.StageTransitionView>(r.History.Count);
        foreach (var h in r.History)
        {
            if (!h.HasSequence || !h.HasApprovedBy || !h.HasReason || !h.HasOccurredAt
                || Stage(h.FromStage) is not { } from || Stage(h.ToStage) is not { } to
                || !DateTimeOffset.TryParse(h.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                return null;
            history.Add(new HttpStageGateController.StageTransitionView(h.Sequence, from, to, Kind(h.Kind), h.ApprovedBy, at, h.Reason));
        }

        HttpStageGateController.Stage1GateCriteriaView? criteria = null;
        if (r.Stage1Criteria is { } c)
        {
            if (!c.HasTargetTradingDays || !c.HasMinimumTradeCount || !c.HasMaximumTradingDays || !c.HasBelowStatisticalBasis)
                return null;
            criteria = new HttpStageGateController.Stage1GateCriteriaView(
                c.TargetTradingDays, c.MinimumTradeCount, c.MaximumTradingDays, c.BelowStatisticalBasis);
        }

        return new HttpStageGateController.StageGateStatusView(
            current,
            new HttpStageGateController.StageSettingsView(settingsStage, Mode(settings.Mode), capRatio),
            history,
            new HttpStageGateController.PromotionAssessmentView(target, promotion.Eligible, [.. promotion.UnmetCriteria.Select(Criterion)]),
            new HttpStageGateController.WithdrawalAssessmentView(
                withdrawal.Triggered, withdrawal.HasReason ? Reason(withdrawal.Reason) : null, withdrawal.HaltNewEntries, proposed),
            criteria);
    }

    // ---- 報告書: 入れ替え案（GET /reports/policy-revisions/watchlist-proposal と同じ射影） ----

    internal static HttpPolicyRevisionController.ProposalView? ToProposalView(ReportProto.GetWatchlistProposalResponse r)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (!r.HasAttemptId || !Guid.TryParse(r.AttemptId, out var attemptId) || !r.HasReportVersion || !r.HasApplyRecorded)
            return null;

        return new HttpPolicyRevisionController.ProposalView(
            attemptId,
            r.HasPeriodKey ? r.PeriodKey : null,
            r.ReportVersion,
            [.. r.Changes.Select(c => new HttpPolicyRevisionController.WatchlistChangeItem(
                c.HasAction ? c.Action : null, c.HasSymbol ? c.Symbol : null, c.HasReason ? c.Reason : null))],
            r.Snapshot is { } snapshot
                ? [.. snapshot.Items.Select(e => new HttpPolicyRevisionController.SnapshotItem(
                    e.HasSymbol ? e.Symbol : null, e.HasMarket ? e.Market : null))]
                : null,
            r.ApplyRecorded);
    }

    // ---- 市場監視: 監視銘柄（REST の HttpMarketMonitorWatchlistController と同じ射影） ----

    internal static HttpMarketMonitorWatchlistController.SymbolView ToSymbolView(MonitorProto.WatchlistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new HttpMarketMonitorWatchlistController.SymbolView(item.HasSymbol ? item.Symbol : null, Market(item.Market));
    }

    // ---- 列挙（名前で写す） ----

    // TradingStage（C# 0〜3）。未指定・未知は null（段階は表示の主語であり、「不明」を既定の Stage 0 と読まない）。
    internal static int? Stage(RiskProto.TradingStage value) => value switch
    {
        RiskProto.TradingStage.Stage0Verification => 0,
        RiskProto.TradingStage.Stage1Simulate => 1,
        RiskProto.TradingStage.Stage2MinimalLive => 2,
        RiskProto.TradingStage.Stage3ScaledLive => 3,
        _ => null,
    };

    internal static int Mode(RiskProto.BrokerProvider value) => value switch
    {
        RiskProto.BrokerProvider.InternalPaper => (int)BrokerProvider.InternalPaper,
        RiskProto.BrokerProvider.MoomooReal => (int)BrokerProvider.MoomooReal,
        RiskProto.BrokerProvider.MoomooSimulate => (int)BrokerProvider.MoomooSimulate,
        _ => Unknown,
    };

    // StageTransitionKind（C# 0=Promotion・1=Demotion・2=ShortSellReleaseVerdict）。
    internal static int Kind(RiskProto.StageTransitionKind value) => value switch
    {
        RiskProto.StageTransitionKind.Promotion => 0,
        RiskProto.StageTransitionKind.Demotion => 1,
        RiskProto.StageTransitionKind.ShortSellReleaseVerdict => 2,
        _ => Unknown,
    };

    // StageGateCriterion（C# の序数。1 は廃止済みで線上に無い）。
    internal static int Criterion(RiskProto.StageGateCriterion value) => value switch
    {
        RiskProto.StageGateCriterion.BacktestNotPassed => 0,
        RiskProto.StageGateCriterion.ControlViolationsPresent => 2,
        RiskProto.StageGateCriterion.SlippageOrCostExceeded => 3,
        RiskProto.StageGateCriterion.DailyLossLimitViolated => 4,
        RiskProto.StageGateCriterion.NoUserApproval => 5,
        RiskProto.StageGateCriterion.PromotionMustBeSequential => 6,
        RiskProto.StageGateCriterion.TargetIsCurrentStage => 7,
        RiskProto.StageGateCriterion.AlreadyAtTopStage => 8,
        RiskProto.StageGateCriterion.Stage1TradingDaysInsufficient => 9,
        RiskProto.StageGateCriterion.Stage1TradeCountInsufficient => 10,
        RiskProto.StageGateCriterion.Stage1ExtensionExhausted => 11,
        RiskProto.StageGateCriterion.ControlViolationCountUnavailable => 12,
        _ => Unknown,
    };

    // WithdrawalReason（C# 0=DrawdownBreachedMultiple・2=Stage1ExtensionExhausted。1 は廃止済み）。
    internal static int Reason(RiskProto.WithdrawalReason value) => value switch
    {
        RiskProto.WithdrawalReason.DrawdownBreachedMultiple => 0,
        RiskProto.WithdrawalReason.Stage1ExtensionExhausted => 2,
        _ => Unknown,
    };

    internal static Market? Market(MonitorProto.Market value) => value switch
    {
        MonitorProto.Market.Japan => AiStockTrading.Shared.Contracts.Trading.Market.Japan,
        MonitorProto.Market.UnitedStates => AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates,
        _ => null,
    };

    // 不変文化の 10 進。欠落・空・読めない値は null（0 にしない）。前後の空白は拒む（送り手は空白を付けない。IADR-0447 と同じ読み方）。
    internal static decimal? Decimal(bool has, string text) =>
        has && !string.IsNullOrEmpty(text) && text.Trim().Length == text.Length
        && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
