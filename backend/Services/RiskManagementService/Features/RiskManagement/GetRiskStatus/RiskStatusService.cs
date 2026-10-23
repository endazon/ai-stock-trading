using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement.GetRiskStatus;

// FR-10, UC-07, ADR-0009: `/status`（詳細設計07）が参照する稼働状態を集約する。表示専用（副作用なし）。
// 3 統制（kill switch / 日次損失ロックアウト / 一時停止）の現在状態・優先順位・段階・当日損益・上限使用率・
// ポジションを 1 回の一貫スナップショットに束ねる。優先順位は表示用で、新規建て停止の判定は OR。
public sealed class RiskStatusService(
    PortfolioSnapshotBuilder snapshotBuilder,
    IRiskSettingsStore settingsStore,
    IPauseStore pauseStore,
    ILockoutStore lockoutStore,
    IStageGateStore stageGateStore,
    IPortfolioLedgerStore ledger,
    IClock clock,
    // FR-10, SC-03, #1423 M-3, ADR-0073 決定3, IADR-0581: 基準資金とその帰結（観測の欠け）を**1 回の読み出し**から表示するため。
    // 🔴 必須である（省略可にすると、結線が外れても欠けを黙って表示しない形で試験が通る）。
    ICapitalBaselineStore capitalBaseline)
{
    public RiskStatusView Build()
    {
        var snapshot = snapshotBuilder.Build();
        var settings = settingsStore.GetCurrent();
        var pause = pauseStore.GetState();
        var stage = stageGateStore.Load().CurrentStage;

        // 日次損失ロックアウトは「当日有効か」で判定する（翌営業日の解除日 ReleaseOn 未満なら有効）。
        // 表示専用のため状態の掃除（Clear）は行わない（掃除は発注審査経路の責務・OrderScreeningService）。
        // #337（#249 吸収）, IADR-0246: 表示は市場を特定できないため、**最も遅れている市場の現地取引日**で
        // 判定する（保守側）。JST（先行側）で判定すると、米国セッションではまだ効いている統制を
        // 「解除済み」と表示してしまう。発注審査の実判定は注文の市場ごと（OrderScreeningService）。
        var lockout = lockoutStore.Get();
        var lockoutActive = lockout is not null && lockout.IsActiveOn(TradingDay.EarliestCurrent(clock.UtcNow));

        // 優先順位（ADR-0009・重い順）: kill switch > 日次損失ロックアウト > 一時停止。表示の見出し用。
        var activeControl = snapshot.KillSwitchEngaged
            ? ActiveTradingControl.KillSwitch
            : lockoutActive
                ? ActiveTradingControl.DailyLossLockout
                : pause.Paused
                    ? ActiveTradingControl.Pause
                    : ActiveTradingControl.None;

        // 新規建て停止は 3 統制の OR。手仕舞い・損切りは本フラグに関わらず継続する。
        var newEntriesBlocked = snapshot.KillSwitchEngaged || lockoutActive || pause.Paused;

        // FR-11, SC-03, ADR-0041 決定 1, #870, IADR-0360 決定 5: 当日のシステム外売買の取り込み件数。
        // 「当日」は**取り込みの市場の現地取引日**で判定する（当日実現損益・日次発注枠と同じ境界。IADR-0246）。
        // 🔴 表示は市場を特定できないため、取り込み 1 件ごとに**その市場の取引日**と**同じ市場の今日**を比べる
        //（1 つの市場の暦で全件を切ると、同一瞬間でも JST と ET で日付が違う分だけ数え違える）。
        var now = clock.UtcNow;
        var driftAdoptionCountToday = ledger.GetDriftAdoptions()
            .Count(a => PortfolioProjection.TradeDate(a.AdoptedAt, a.Market)
                     == PortfolioProjection.TradeDate(now, a.Market));

        // FR-10, SC-03, #1423 M-3, ADR-0073 決定3, IADR-0581: 直前の米国東部暦日に成功した観測が無いために新規建てを
        // 止めていることを画面へ出す。🔴 計器を動かさない口（Read）で**1 回だけ**読み、表示の基準資金（と上限の実額）と欠けの印を
        // 同じ結果から作る（スナップショット側の読み出しと 2 回読むと、間に行が書かれたとき「資金あり・欠けあり」が並び得る）。
        var reading = capitalBaseline.Read();
        var capital = reading.Baseline?.EquityInBase;
        var observationMissing = reading.ObservationMissing;

        return new RiskStatusView(
            KillSwitchEngaged: snapshot.KillSwitchEngaged,
            DailyLossLockoutActive: lockoutActive,
            LockoutReleaseOn: lockout?.ReleaseOn,
            TradingPaused: pause.Paused,
            ActiveControl: activeControl,
            NewEntriesBlocked: newEntriesBlocked,
            Stage: stage,
            // FR-20, #334: 発注先は段階と独立の軸であり、設定から読む（段階からは導出しない）。
            BrokerProvider: settings.BrokerProvider,
            DailyRealizedPnl: snapshot.DailyRealizedPnl,
            UnrealizedPnl: snapshot.UnrealizedPnl,
            DailyPnl: snapshot.DailyRealizedPnl + snapshot.UnrealizedPnl,
            Capital: capital,
            DailyOrderedAmount: snapshot.DailyOrderedAmount,
            // FR-10, #329, IADR-0130 決定1: 上限は equity 比のため、表示も equity から解決した実額を載せる。
            // #869, ADR-0041 決定2: equity が未供給（口座を照会できていない）なら実額も未供給である。
            // **0 で埋めない**——「上限 0」と「上限が分からない」は別の事実である。
            MaxOrderAmount: capital is { } equity
                ? settings.Limits.MaxOrderAmountFor(equity)
                : null,
            MaxDailyOrderAmount: capital is { } dailyEquity
                ? settings.Limits.MaxDailyOrderAmountFor(dailyEquity)
                : null,
            DrawdownRatio: snapshot.DrawdownRatio,
            MaxDrawdownRatio: settings.Limits.MaxDrawdownRatio,
            OpenPositionCount: snapshot.OpenPositionCount,
            MaxOpenPositions: settings.Limits.MaxOpenPositions,
            // FR-10, SC-03, ADR-0040 決定1, #819: 選択中の損切りの実行機構（参照専用）。
            StopLossMethod: settings.StopLossMethod,
            // FR-11, SC-03, ADR-0041 決定 1, #870: 当日の取り込み件数（参照専用）。
            DriftAdoptionCountToday: driftAdoptionCountToday,
            CapitalBaselineObservationMissing: observationMissing,
            CapitalBaselineLatestTradingDay: observationMissing ? reading.LatestTradingDay : null);
    }
}
