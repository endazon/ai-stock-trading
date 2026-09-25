namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-09, #879, IADR-0424 決定1: 見送り（Events.OrderDispatchForgone）が運ぶ値。イベントではないので Trading に置く
// （Events 名前空間の record はイベントの母集合として検査される。PositionDriftItem と同じ置き方）。
/// <summary>
/// 🔴 FR-10, #879, IADR-0424 決定1: 建玉照会の不明で決済を見送った時点で、発注執行の保護記録（protective_stop_orders）が
/// その建玉（決済の反対方向＝エントリー方向・同一銘柄・同一市場の Active な行）について何を言っていたか。
/// <para>
/// 数量は記録の<b>実効数量</b>（EffectiveProtectedQuantity＝帳簿の主張から未確定の外部要因の減少を引いた値。ClaimedFor と同じ）の合計であって、ブローカーで注文が生きていることの確認ではない
/// （照会できないので確かめられない）。<see cref="BrokerSideQuantity"/> はブローカー側の注文（S0・S3）、
/// <see cref="SoftwareStopQuantity"/> はソフトウェア逆指値（S1）。🔴 S1 の決済も建玉照会の不明のあいだは据え置かれる
/// （SoftwareStopExecutor）ため、照会不明のあいだに効く保護はブローカー側の注文だけである。
/// </para>
/// </summary>
public record ForgoneCloseProtection(
    ForgoneCloseProtectionStatus Status,
    int BrokerSideQuantity,
    int SoftwareStopQuantity);

/// <summary>
/// 🔴 FR-10, #879, IADR-0424 決定1: 保護の記録の読み（不明・無し・有りを混ぜない）。
/// <b>序数 0 は <see cref="Unknown"/></b> —— 既定値へ落ちた値は「分からない」側へ倒れる。<b>末尾へ追加する</b>。
/// </summary>
public enum ForgoneCloseProtectionStatus
{
    /// <summary>記録を読めなかった（記録ストアの無い構成・読み取りの失敗）。保護の有無は分からない。</summary>
    Unknown,

    /// <summary>その建玉の Active な保護記録が 1 件も無い（S2 で建てた建玉など）。システムの保護レグは無い。</summary>
    NoneRecorded,

    /// <summary>Active な保護記録がある。株数は <see cref="ForgoneCloseProtection"/> の 2 つの数量。</summary>
    Recorded,
}
