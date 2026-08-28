using RiskManagementService.Application.Ports;

namespace RiskManagementService.Application.Services;

// FR-05, FR-09, FR-10, #292, #305, IADR-0118, IADR-0124: 乖離を報告するかの判定（純関数）。
//
// 判定の意味論は IADR-0118 のまま。2 つの雑音を落とす。
//   1. **一過性の未反映**: 発注してから約定が台帳へ届くまでの間、台帳とブローカは正当にズレる。
//      同一内容が連続で観測されたときだけ報告することで、通常運行のたびに鳴るのを防ぐ。
//   2. **通知過多**: 解消されない乖離は毎巡回で観測され続ける。前回報告した内容と同一なら再報告しない。
//
// 状態の保持先だけが変わった（プロセス内 → durable な単一行・IADR-0124）。ここは状態を受け取り次状態を返すだけで、
// 永続も並行制御も持たない（テストが状態遷移そのものを直接固定できる）。
public static class PositionDriftDecision
{
    /// <param name="current">現在の追跡状態。</param>
    /// <param name="signature">今回観測した乖離の正準シグネチャ。**空文字は「乖離なし（解消）」**を意味する。</param>
    /// <param name="requiredConsecutiveObservations">報告に必要な連続観測回数（1 以上）。</param>
    /// <returns>保存すべき次状態と、今回報告すべきか。</returns>
    public static (PositionDriftState Next, bool ShouldReport) Decide(
        PositionDriftState current,
        string signature,
        int requiredConsecutiveObservations)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(signature);

        if (signature.Length == 0)
        {
            // 解消。報告済みを忘れることで、同じ乖離が再発したときに再び報告できる。
            // 連続回数は「何の」連続かが無いので 0 へ戻す。これにより乖離ゼロが続く間は状態が完全に不変になり、
            // 呼び出し側が「変化なし＝書かない」で巡回ごとの無駄な永続化を避けられる。
            return (
                current with
                {
                    ObservedSignature = string.Empty,
                    ConsecutiveCount = 0,
                    ReportedSignature = string.Empty,
                },
                false);
        }

        var next = signature == current.ObservedSignature
            ? current with { ConsecutiveCount = current.ConsecutiveCount + 1 }
            : current with { ObservedSignature = signature, ConsecutiveCount = 1 };

        if (next.ConsecutiveCount < requiredConsecutiveObservations || signature == next.ReportedSignature)
        {
            return (next, false);
        }

        return (next with { ReportedSignature = signature }, true);
    }
}
