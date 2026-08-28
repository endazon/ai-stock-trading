using RiskManagementService.Domain;

namespace RiskManagementService.Application.Ports;

// FR-10, UC-06, #330, IADR-0133 決定5: 維持率判定の実測入力（純資産・建玉・必要証拠金）の供給元。
//
// 供給元はブローカー（moomoo）の口座照会であり**現時点では実装が無い**（#342 の PoC・#331 の発注執行が担当）。
// 実装が無い間は <c>null</c>（供給なし）を返し、自動縮小は 1 株も決済しない。
//
// **フェイルクローズの向きは統制ごとに違う。** 「維持率が確認できないなら空売りを積み増さない」（IADR-0131 決定4）は
// 安全側だが、「維持率が分からないから決済する」は安全側ではない（誤作動の害が**不可逆**。UC-06 の 2 統制比較表）。
public interface IMaintenanceMarginSnapshotSource
{
    /// <summary>現在の維持率スナップショット。供給経路が無い・取得できない場合は <c>null</c>。</summary>
    MaintenanceMarginSnapshot? GetCurrent();
}
