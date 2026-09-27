using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement.GetDriftAdoptions;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Features.RiskManagement;

// NFR, FR-10, IADR-0331 決定 2, IADR-0427 決定 3・4, #997 (#753): 読み取りの応答型 → 線上表現の写像（提供側）。
//
// 🔴 **原則 A**: 受け手は「項目が無い」を「不明」と読む（REST の nullable DTO と同じ）。したがって提供側は
//   - 値が在る項目は**必ず設定する**（0 でも設定する＝`Has*` が立つ。proto3 の optional は 0 を「在る」として運ぶ）、
//   - 値が無い（C# の null）項目は**設定しない**（`Has*` が立たない＝受け手は「不明」と読む）。
// 🔴 **列挙は名前で写す。** C# の 0（Japan / Buy / Open / InternalPaper / BrokerStopOrder / Stage0Verification）は実在の値であり、
// proto の 0 は「未指定」である。番号のまま `(Proto.Market)(int)x` と写すと、日本が「未指定」に化ける。
public static class RiskReadWireMapping
{
    // decimal → 線上。不変文化を明示する（既定文化だと小数点が `,` になる環境で壊れる。IADR-0331 決定 2）。
    public static string ToWire(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string ToWire(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // 往復書式（`O`）。オフセットを保つ（REST の System.Text.Json と同じくオフセットごと運ぶ）。
    public static string ToWire(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static Proto.Market ToProto(Market value) => value switch
    {
        Market.Japan => Proto.Market.Japan,
        Market.UnitedStates => Proto.Market.UnitedStates,
        _ => Proto.Market.Unspecified,
    };

    public static Proto.TradeSide ToProto(TradeSide value) => value switch
    {
        TradeSide.Buy => Proto.TradeSide.Buy,
        TradeSide.Sell => Proto.TradeSide.Sell,
        _ => Proto.TradeSide.Unspecified,
    };

    public static Proto.PositionEffect ToProto(PositionEffect value) => value switch
    {
        PositionEffect.Open => Proto.PositionEffect.Open,
        PositionEffect.Close => Proto.PositionEffect.Close,
        _ => Proto.PositionEffect.Unspecified,
    };

    public static Proto.BrokerProvider ToProto(BrokerProvider? value) => value switch
    {
        BrokerProvider.InternalPaper => Proto.BrokerProvider.InternalPaper,
        BrokerProvider.MoomooReal => Proto.BrokerProvider.MoomooReal,
        BrokerProvider.MoomooSimulate => Proto.BrokerProvider.MoomooSimulate,
        _ => Proto.BrokerProvider.Unspecified,
    };

    public static Proto.StopLossExecutionMethod ToProto(StopLossExecutionMethod value) => value switch
    {
        StopLossExecutionMethod.BrokerStopOrder => Proto.StopLossExecutionMethod.BrokerStopOrder,
        StopLossExecutionMethod.SoftwareStop => Proto.StopLossExecutionMethod.SoftwareStop,
        StopLossExecutionMethod.NoProtectiveStop => Proto.StopLossExecutionMethod.NoProtectiveStop,
        StopLossExecutionMethod.AlternativeBrokerOrderType => Proto.StopLossExecutionMethod.AlternativeBrokerOrderType,
        _ => Proto.StopLossExecutionMethod.Unspecified,
    };

    public static Proto.TradingStage ToProto(TradingStage value) => value switch
    {
        TradingStage.Stage0Verification => Proto.TradingStage.Stage0Verification,
        TradingStage.Stage1Simulate => Proto.TradingStage.Stage1Simulate,
        TradingStage.Stage2MinimalLive => Proto.TradingStage.Stage2MinimalLive,
        TradingStage.Stage3ScaledLive => Proto.TradingStage.Stage3ScaledLive,
        _ => Proto.TradingStage.Unspecified,
    };

    // ---- 段 5（IADR-0449 決定 3）: 段階ゲートの現況と稼働状態（Discord ボットが読む項目） ----

    public static Proto.StageTransitionKind ToProto(StageTransitionKind value) => value switch
    {
        StageTransitionKind.Promotion => Proto.StageTransitionKind.Promotion,
        StageTransitionKind.Demotion => Proto.StageTransitionKind.Demotion,
        StageTransitionKind.ShortSellReleaseVerdict => Proto.StageTransitionKind.ShortSellReleaseVerdict,
        _ => Proto.StageTransitionKind.Unspecified,
    };

    public static Proto.StageGateCriterion ToProto(StageGateCriterion value) => value switch
    {
        StageGateCriterion.BacktestNotPassed => Proto.StageGateCriterion.BacktestNotPassed,
        StageGateCriterion.ControlViolationsPresent => Proto.StageGateCriterion.ControlViolationsPresent,
        StageGateCriterion.SlippageOrCostExceeded => Proto.StageGateCriterion.SlippageOrCostExceeded,
        StageGateCriterion.DailyLossLimitViolated => Proto.StageGateCriterion.DailyLossLimitViolated,
        StageGateCriterion.NoUserApproval => Proto.StageGateCriterion.NoUserApproval,
        StageGateCriterion.PromotionMustBeSequential => Proto.StageGateCriterion.PromotionMustBeSequential,
        StageGateCriterion.TargetIsCurrentStage => Proto.StageGateCriterion.TargetIsCurrentStage,
        StageGateCriterion.AlreadyAtTopStage => Proto.StageGateCriterion.AlreadyAtTopStage,
        StageGateCriterion.Stage1TradingDaysInsufficient => Proto.StageGateCriterion.Stage1TradingDaysInsufficient,
        StageGateCriterion.Stage1TradeCountInsufficient => Proto.StageGateCriterion.Stage1TradeCountInsufficient,
        StageGateCriterion.Stage1ExtensionExhausted => Proto.StageGateCriterion.Stage1ExtensionExhausted,
        StageGateCriterion.ControlViolationCountUnavailable => Proto.StageGateCriterion.ControlViolationCountUnavailable,
        _ => Proto.StageGateCriterion.Unspecified,
    };

    public static Proto.WithdrawalReason ToProto(WithdrawalReason value) => value switch
    {
        WithdrawalReason.DrawdownBreachedMultiple => Proto.WithdrawalReason.DrawdownBreachedMultiple,
        WithdrawalReason.Stage1ExtensionExhausted => Proto.WithdrawalReason.Stage1ExtensionExhausted,
        _ => Proto.WithdrawalReason.Unspecified,
    };

    public static Proto.GetStageGateResponse ToProto(StageGateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var response = new Proto.GetStageGateResponse
        {
            CurrentStage = ToProto(status.CurrentStage),
            CurrentSettings = new Proto.StageSettingsRecord
            {
                Stage = ToProto(status.CurrentSettings.Stage),
                Mode = ToProto((BrokerProvider?)status.CurrentSettings.Mode),
                CapitalCapRatio = ToWire(status.CurrentSettings.CapitalCapRatio),
            },
            Promotion = new Proto.PromotionAssessmentRecord { Eligible = status.Promotion.Eligible },
            Withdrawal = new Proto.WithdrawalAssessmentRecord
            {
                Triggered = status.Withdrawal.Triggered,
                HaltNewEntries = status.Withdrawal.HaltNewEntries,
            },
            Stage1Criteria = new Proto.Stage1CriteriaRecord
            {
                TargetTradingDays = status.Stage1Criteria.TargetTradingDays,
                MinimumTradeCount = status.Stage1Criteria.MinimumTradeCount,
                MaximumTradingDays = status.Stage1Criteria.MaximumTradingDays,
                BelowStatisticalBasis = status.Stage1Criteria.BelowStatisticalBasis,
            },
        };

        // 🔴 C# の null（最上段・理由なし・提案なし）は**設定しない**（受け手は欠落を REST の null と同じに読む）。
        if (status.Promotion.TargetStage is { } target)
            response.Promotion.TargetStage = ToProto(target);
        response.Promotion.UnmetCriteria.AddRange(status.Promotion.UnmetCriteria.Select(ToProto));
        if (status.Withdrawal.Reason is { } reason)
            response.Withdrawal.Reason = ToProto(reason);
        if (status.Withdrawal.ProposedStage is { } proposed)
            response.Withdrawal.ProposedStage = ToProto(proposed);

        response.History.AddRange(status.History.Select(t =>
        {
            var row = new Proto.StageTransitionRecord
            {
                Sequence = t.Sequence,
                FromStage = ToProto(t.FromStage),
                ToStage = ToProto(t.ToStage),
                Kind = ToProto(t.Kind),
                OccurredAt = ToWire(t.OccurredAtUtc),
            };
            if (t.ApprovedBy is not null)
                row.ApprovedBy = t.ApprovedBy;
            if (t.Reason is not null)
                row.Reason = t.Reason;
            return row;
        }));
        return response;
    }

    public static Proto.GetRiskStatusResponse ToProto(GetRiskStatus.RiskStatusView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var response = new Proto.GetRiskStatusResponse
        {
            KillSwitchEngaged = view.KillSwitchEngaged,
            DailyLossLockoutActive = view.DailyLossLockoutActive,
            TradingPaused = view.TradingPaused,
            NewEntriesBlocked = view.NewEntriesBlocked,
            Stage = ToProto(view.Stage),
            DailyRealizedPnl = ToWire(view.DailyRealizedPnl),
            UnrealizedPnl = ToWire(view.UnrealizedPnl),
            DailyPnl = ToWire(view.DailyPnl),
            DailyOrderedAmount = ToWire(view.DailyOrderedAmount),
            DrawdownRatio = ToWire(view.DrawdownRatio),
            MaxDrawdownRatio = ToWire(view.MaxDrawdownRatio),
            OpenPositionCount = view.OpenPositionCount,
            MaxOpenPositions = view.MaxOpenPositions,
        };

        // 🔴 null（解除日が未定・口座を照会できず上限を解決できない）は**設定しない**。0 と書くと「上限 0」になる（#990）。
        if (view.LockoutReleaseOn is { } releaseOn)
            response.LockoutReleaseOn = ToWire(releaseOn);
        if (view.MaxDailyOrderAmount is { } maxDaily)
            response.MaxDailyOrderAmount = ToWire(maxDaily);
        return response;
    }

    public static Proto.OpenPositionRow ToProto(OpenPositionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var row = new Proto.OpenPositionRow
        {
            Market = ToProto(view.Market),
            Side = ToProto(view.Side),
            Quantity = view.Quantity,
            EntryPrice = ToWire(view.EntryPrice),
            StopLossPrice = ToWire(view.StopLossPrice),
        };
        if (view.Symbol is not null)
            row.Symbol = view.Symbol;
        return row;
    }

    public static Proto.WorkingEntryOrderRow ToProto(WorkingEntryOrderView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var row = new Proto.WorkingEntryOrderRow
        {
            Market = ToProto(view.Market),
            Side = ToProto(view.Side),
            RemainingQuantity = view.RemainingQuantity,
            Price = ToWire(view.Price),
            ApprovedAt = ToWire(view.ApprovedAt),
        };
        if (view.Symbol is not null)
            row.Symbol = view.Symbol;
        return row;
    }

    public static Proto.GetSizingContextResponse ToProto(SizingContextView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var response = new Proto.GetSizingContextResponse
        {
            ConsecutiveLosses = view.ConsecutiveLosses,
            DrawdownRatio = ToWire(view.DrawdownRatio),
            Mode = ToProto(view.Mode),
            StopLossMethod = ToProto(view.StopLossMethod),
        };

        // 🔴 null（口座を照会できていない・残枠を解決できない）は**設定しない**。0 と書くと「枠を使い切った」になる。
        if (view.Capital is { } capital)
            response.Capital = ToWire(capital);
        if (view.StageCapitalRemaining is { } stage)
            response.StageCapitalRemaining = ToWire(stage);
        if (view.DailyOrderRemaining is { } daily)
            response.DailyOrderRemaining = ToWire(daily);

        if (view.Limits is { } limits)
        {
            response.Limits = new Proto.RiskLimits
            {
                MaxOrderAmountRatio = ToWire(limits.MaxOrderAmountRatio),
                MaxDailyOrderAmountRatio = ToWire(limits.MaxDailyOrderAmountRatio),
                MaxOpenPositions = limits.MaxOpenPositions,
                DailyLossLimitRatio = ToWire(limits.DailyLossLimitRatio),
                PerTradeRiskRatio = ToWire(limits.PerTradeRiskRatio),
                MaxDrawdownRatio = ToWire(limits.MaxDrawdownRatio),
                LosingStreakThreshold = limits.LosingStreakThreshold,
                LosingStreakSizeFactor = ToWire(limits.LosingStreakSizeFactor),
            };
        }

        return response;
    }

    public static Proto.PeriodFill ToProto(LedgerFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        var row = new Proto.PeriodFill
        {
            Market = ToProto(fill.Market),
            Side = ToProto(fill.Side),
            PositionEffect = ToProto(fill.PositionEffect),
            Quantity = fill.Quantity,
            Price = ToWire(fill.Price),
            ExecutedAt = ToWire(fill.ExecutedAt),
            FxRateToBase = ToWire(fill.FxRateToBase),
            DecisionId = fill.DecisionId.ToString("D", CultureInfo.InvariantCulture),
            // 🔴 null（旧版の行・列追加前の行）は未指定＝受け手は「不明」と読む（どちらの段にも算入しない）。
            Provider = ToProto(fill.Provider),
        };
        if (fill.Symbol is not null)
            row.Symbol = fill.Symbol;
        // 🔴 null（未記録）は設定しない。1 円/ドルのような既定へ倒す正当な値が無い。
        if (fill.FxRateBaseToDisplay is { } display)
            row.FxRateBaseToDisplay = ToWire(display);
        return row;
    }

    public static Proto.DriftAdoption ToProto(DriftAdoptionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var row = new Proto.DriftAdoption
        {
            AdoptionId = view.AdoptionId.ToString("D", CultureInfo.InvariantCulture),
            Market = ToProto(view.Market),
            Side = ToProto(view.Side),
            Quantity = view.Quantity,
            LedgerQuantityBefore = view.LedgerQuantityBefore,
            BrokerQuantity = view.BrokerQuantity,
            ObservedAt = ToWire(view.ObservedAt),
            AdoptedAt = ToWire(view.AdoptedAt),
        };
        if (view.Symbol is not null)
            row.Symbol = view.Symbol;
        if (view.Actor is not null)
            row.Actor = view.Actor;
        if (view.Reason is not null)
            row.Reason = view.Reason;
        return row;
    }

    public static Proto.BuyInInferenceRow ToProto(BuyInInferenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var row = new Proto.BuyInInferenceRow
        {
            Id = record.Id.ToString("D", CultureInfo.InvariantCulture),
            Market = ToProto(record.Market),
            LedgerShortQuantity = record.LedgerShortQuantity,
            BrokerShortQuantity = record.BrokerShortQuantity,
            InFlightCloseQuantity = record.InFlightCloseQuantity,
            UnexplainedQuantity = record.UnexplainedQuantity,
            NewlyInferredQuantity = record.NewlyInferredQuantity,
            InferredOn = ToWire(record.InferredOn),
            ObservedAt = ToWire(record.ObservedAt),
            InferredAt = ToWire(record.InferredAt),
        };
        if (record.Symbol is not null)
            row.Symbol = record.Symbol;
        if (record.BanUntil is { } banUntil)
            row.BanUntil = ToWire(banUntil);
        return row;
    }

    public static Proto.SessionUptimeDay ToProto(OpenDSessionUptimeDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return new Proto.SessionUptimeDay
        {
            SessionDateEasternTime = ToWire(day.SessionDateEasternTime),
            UptimeRatio = ToWire(day.UptimeRatio),
        };
    }
}
