using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Features.RiskManagement;

// FR-05, FR-09, FR-10, #292, #305, IADR-0118, IADR-0124: 乖離を報告するかの判断。
//
// 判定そのものは純関数 PositionDriftDecision が持ち、本クラスは「シグネチャの正準化」と
// 「状態の読み取り → 判定 → 保存」の束ねに徹する。状態は durable なストア（既定は DB 単一行）にあり、
// **レプリカ間で一貫**する（IADR-0124）。インメモリ時代は replicas>1 で連続観測が Pod へ分散し、
// 乖離が例外もログも出さずに恒久未報告になり得た（#305）。
public sealed class PositionDriftTracker(
    IPositionDriftStateStore store,
    ILogger<PositionDriftTracker> logger,
    int requiredConsecutiveObservations = PositionDriftTracker.DefaultRequiredConsecutiveObservations)
{
    /// <summary>
    /// 報告に必要な連続観測回数。構成キーにはしない（運用で触る値ではなく、下げれば雑音・上げれば検知が遅れるだけ）。
    /// </summary>
    public const int DefaultRequiredConsecutiveObservations = 2;

    // 0・負値で「決して報告しない」状態を作らせない（検知器が黙る設定を許さない）。
    private readonly int _required = Math.Max(1, requiredConsecutiveObservations);

    /// <summary>今回観測した乖離を報告すべきなら true。乖離なし（空）は常に false。</summary>
    public bool ShouldReport(IReadOnlyList<PositionDriftItem> drifts)
    {
        ArgumentNullException.ThrowIfNull(drifts);

        var signature = Signature(drifts);
        var current = store.Get();
        var (next, shouldReport) = PositionDriftDecision.Decide(current, signature, _required);

        if (next == current)
        {
            // 状態に変化なし（乖離ゼロが続く巡回）。書かない＝無駄な永続化と版の空回りを避ける。
            return shouldReport;
        }

        if (store.TrySave(next))
        {
            return shouldReport;
        }

        // 別レプリカが先に同じ状態を進めた。この観測は捨てて報告しない（リトライしない・IADR-0124 決定 2）。
        // 競合しても必ずどれか 1 つは勝つため状態は単調に前進し、捨てた内容は乖離が解消するまで毎巡回で
        // 再観測される。失うのは最大 1 巡回分の時間であって報告そのものではない。無言にはしない。
        logger.LogDebug(
            "建玉突合: 追跡状態の更新が並行更新に負けました（他レプリカが先行）。今回の観測は報告せず次の巡回で数え直します。連続 {Consecutive} 回目・乖離 {Count} 件。",
            next.ConsecutiveCount, drifts.Count);
        return false;
    }

    // 列挙順はブローカ応答に依存するため、順序に依らない正準形にする
    //（順序差を「内容が変わった」と誤認すると連続条件が永久に満たされない）。
    // 乖離ゼロなら空文字＝「解消」を意味する（乖離が 1 件でもあれば必ず非空になる）。
    private static string Signature(IReadOnlyList<PositionDriftItem> drifts) =>
        string.Join(
            "|",
            drifts
                .Select(d => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{d.Symbol}:{(int)d.Market}:{d.LedgerQuantity}:{d.BrokerQuantity}:{(int)d.Kind}"))
                .OrderBy(s => s, StringComparer.Ordinal));
}
