namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-05, FR-09, FR-10, #305, IADR-0121: 建玉乖離の報告可否を決める追跡状態。**レプリカ間で共有する**。
//
// IADR-0118 はこの状態をプロセス内に置いていたが、`BrokerPositionsObserved` は consumer クラス名から導かれる
// 単一キューで受ける（IADR-0106）ため、replicas>1 では観測が Pod へ分散し「連続 N 回」条件が満たされなくなる。
// 例外もログも出ないまま乖離が恒久未報告になるため、durable な単一行（IADR-0085 と同型）へ移した。
public sealed record PositionDriftState(
    string ObservedSignature,
    int ConsecutiveCount,
    string ReportedSignature,
    int Version)
{
    /// <summary>
    /// 未記録の初期状態（未観測・カウント 0・未報告）。fail-safe の向きは「報告しない」ではなく
    /// 「数え直す」＝次の連続観測で必ず報告へ到達する側。
    /// </summary>
    public static readonly PositionDriftState Initial = new(string.Empty, 0, string.Empty, 0);
}

/// <summary>
/// 建玉乖離の追跡状態を保持するストア（単一行）。<see cref="IWithdrawalNotificationStore"/> と同型の durable 重複排除。
/// </summary>
public interface IPositionDriftStateStore
{
    /// <summary>現在の追跡状態を返す。未記録なら <see cref="PositionDriftState.Initial"/>。</summary>
    PositionDriftState Get();

    /// <summary>
    /// 状態を保存する。<see cref="PositionDriftState.Version"/> が保存先の現在版と一致するときだけ成功し、
    /// 一致しなければ（＝別レプリカが先に状態を進めた）何も書かずに false を返す。
    /// </summary>
    bool TrySave(PositionDriftState state);
}
