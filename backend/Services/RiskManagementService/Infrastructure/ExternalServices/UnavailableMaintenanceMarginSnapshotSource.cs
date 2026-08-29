using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-10, UC-06, #330, IADR-0133 決定5: 維持率の供給元が未実装であることを明示する既定アダプタ。
// **null を返す＝供給なし**であり、自動縮小は発動しない（データが無いのに決済しない）。
// 実装は #342（moomoo の口座照会）・#331（発注執行）が入れる。
public sealed class UnavailableMaintenanceMarginSnapshotSource : IMaintenanceMarginSnapshotSource
{
    public MaintenanceMarginSnapshot? GetCurrent() => null;
}
