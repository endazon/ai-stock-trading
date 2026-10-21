namespace RiskManagementService.Features.RiskManagement.GetTradingHalt;

// 🔴 FR-10, ADR-0009, IADR-0567 決定 2: 停止の状態を**審査と同じストア**から読む（PortfolioSnapshotBuilder が審査へ渡す
// KillSwitchEngaged・TradingPaused と同じ IKillSwitchStore / IPauseStore）。読み取り専用で、書き込み・イベントの発行はしない。
// ストアの読み取りが失敗すれば例外のまま返す（読み取り群のフィルタが 500 へ写し、受け手は「不明」として送らない）。
public sealed class TradingHaltService(IKillSwitchStore killSwitch, IPauseStore pause)
{
    public TradingHaltView Build() => new(killSwitch.GetState().Engaged, pause.GetState().Paused);
}
