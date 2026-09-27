using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-04, FR-13, FR-15, ADR-0044 決定 3, ADR-0046 決定 1・2, #1049, IADR-0442 決定 2, IADR-0282 決定 2:
// 監視設定の行が持つ seed の時刻（SeededAt）と現在の監視銘柄を、**何も書かずに**読むポート。
//
// 🔴 `IMonitoredSymbolStore.GetSettings()` を使わないのは、読むだけで書くことがあるからである（行が無ければ seed を挿入し、
// 空なら再 seed して SeededAt を上書きする）。当時の監視銘柄の再構成は読み取り専用の照会であり、照会が台帳の状態
// （SeededAt を含む）を変えてはならない。
public interface IMonitorSeedRecord
{
    /// <summary>
    /// 監視設定の行の seed の時刻と現在の監視銘柄。<b>行が無ければ null</b>（seed がまだ一度も適用されていない）。
    /// </summary>
    MonitorSeedState? Read();
}

/// <summary>
/// 監視設定の行の状態（読み取り専用の写し）。
/// </summary>
/// <param name="SeededAt">
/// 構成の seed を最後に適用した時刻（IADR-0282 決定 2）。🔴 <b>null は「記録されていない」</b>（2026-09-02 の移行より前に作られた行）であり、
/// 推測で埋めない（ADR-0046 決定 2）。
/// </param>
/// <param name="CurrentSymbols">現在の監視銘柄（行の本文そのまま。再 seed はしない）。</param>
public sealed record MonitorSeedState(DateTimeOffset? SeededAt, IReadOnlyList<MonitoredSymbol> CurrentSymbols);
