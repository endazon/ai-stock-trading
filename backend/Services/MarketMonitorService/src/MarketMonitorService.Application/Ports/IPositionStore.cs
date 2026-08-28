using MarketMonitorService.Domain;

namespace MarketMonitorService.Application.Ports;

// FR-03, FR-10: 損切りライン検知のための保有ポジション（銘柄・建玉方向・数量・損切り価格）の供給。
// 実データはリスク管理（#12・#63 台帳）の GET /risk-controls/open-positions を同期照会して得る（IADR-0030）。
// 同期 HTTP を sync-over-async にしないため非同期とする。依存先障害時は空列（＝損切り検知対象なし）に倒す。
public interface IPositionStore
{
    Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default);
}
