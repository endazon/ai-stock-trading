using RiskManagementService.Domain;


namespace RiskManagementService.Features.RiskManagement.GetSizingContext;

// FR-04, FR-10, IADR-0029: 取引判断へ供給するサイジング文脈を、設定（IRiskSettingsStore）とポートフォリオ状態
// （PortfolioSnapshotBuilder＝#63 台帳の実データ）から導出する。取引判断は本文脈を同期 API で照会する。
//
// 🔴 SC-02, #1407, IADR-0564 決定 1・2: 閉場前の手仕舞いの**効いている値**（保存値があれば保存値・無ければ構成の値）も載せる。
// 手仕舞いを出す常駐（取引判断）はこの値で有効・無効と M を決める。configuredControls の省略は単体試験の直接組み立て用であり（既定＝無効・15 分）、
// 本番の配線で構成の値が渡ることは SessionAndThemeControlsTests（T-10-2952）が固定する。
public sealed class SizingContextService(
    PortfolioSnapshotBuilder snapshotBuilder,
    IRiskSettingsStore settingsStore,
    ConfiguredSessionAndThemeControls? configuredControls = null)
{
    private readonly ConfiguredSessionAndThemeControls _configuredControls = configuredControls ?? ConfiguredSessionAndThemeControls.Default;

    public SizingContextView Build()
    {
        var snapshot = snapshotBuilder.Build();
        var settings = settingsStore.GetCurrent();

        // 段階資金残枠＝段階の発注可能額 − 取得額累計（IADR-0005）。日次発注残枠＝1日上限 − 当日発注累計。
        // いずれも負値は 0。
        // FR-10, #329, IADR-0130 決定1/2: 日次上限は equity 比のため、equity（snapshot.Capital）から解決する。
        // FR-20, #333, IADR-0136: 段階の発注可能額も総資金比のため、同じ equity から解決する。
        // FR-10, #869, ADR-0041 決定2, IADR-0354: equity が未供給（口座を照会できていない）なら残枠も未供給である。
        // **0 で埋めない**——「枠を使い切った」と「枠が分からない」は別の事実であり、
        // 前者はプロンプトにも監査にもそのまま「0」として現れてしまう。
        var stageRemaining = snapshot.Capital is { } stageEquity
            ? Math.Max(0m, settings.Stage.OrderableCapFor(stageEquity) - snapshot.InvestedCapital)
            : (decimal?)null;
        var dailyRemaining = snapshot.Capital is { } dailyEquity
            ? Math.Max(0m, settings.Limits.MaxDailyOrderAmountFor(dailyEquity) - snapshot.DailyOrderedAmount)
            : (decimal?)null;

        return new SizingContextView(
            Capital: snapshot.Capital,
            StageCapitalRemaining: stageRemaining,
            DailyOrderRemaining: dailyRemaining,
            ConsecutiveLosses: snapshot.ConsecutiveLosses,
            DrawdownRatio: snapshot.DrawdownRatio,
            Mode: settings.Stage.Mode,
            Limits: settings.Limits,
            // #854, IADR-0351 決定1: 損切りの実行機構の設定を判断プロンプトの「保護の状態」へ供給する。
            StopLossMethod: settings.StopLossMethod,
            // #1291, IADR-0527 決定3: 審査と同じ区分・上限をサイジングと LLM の前の見送りへ渡す。
            HighVolatility: settings.HighVolatility,
            // #1407, IADR-0564 決定 1: 閉場前の手仕舞いの効いている値（取引判断の常駐が使う）。
            PreCloseFlatten: SessionAndThemeControlResolution.EffectivePreCloseFlatten(settings, _configuredControls));
    }
}
