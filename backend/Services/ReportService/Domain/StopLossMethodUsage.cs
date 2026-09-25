using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-10, FR-12, ADR-0040 決定1, #823, IADR-0422 決定3: 日報 §4「損切りの実行機構（当日）」の集計（純関数）。
//
// 計画（ADR-0040 決定1）: 「どの手法を選んでいるかは、監査ログ・SC-03・**日報**に出す。選択式にした以上、
// 『いまどれで走っているか』が読めなければ観測結果を解釈できない」。
//
// 🔴 **数えるのは承認が運ぶ手法（`OrderApproved.StopLossMethod`＝審査時点で有効だった手法）である。**
// 日報を作る時点の設定値ではない——日中に手法を変えた日を、生成時点の 1 値で塗り潰さない（作業仕様書 §規則 11）。
//
// - **新規建て（`PositionEffect.Open`）の承認だけを数える。** 手仕舞い・維持率割れの自動縮小の承認は既定 S0 を
//   運ぶだけで、手法は効かない（IADR-0342 決定3）。
// - **同じ DecisionId は 1 件**（先勝ち）。承認の再発行が台帳に 2 行残っても二重に数えない。
// - 本文を復元できなかった記録は件数に含めず、別に数える（黙って落とさない）。
//
// #1002, IADR-0429 決定3: 集計に使った承認の**明細**（DecisionId・手法・承認時刻）も持つ。発注執行の解決結果と
// DecisionId で突き合わせ（日報の 2 行目）、承認の JST 暦日で日を数える（月報の日数ベースの内訳）ためである。
// 明細は件数（Counts）と同じ母集合（新規建て・DecisionId で重複を除いたもの）である。
public sealed record StopLossMethodUsage(
    IReadOnlyList<StopLossMethodCount> Counts,
    int UnreadableCount)
{
    /// <summary>
    /// #1002, IADR-0429 決定3: 数えた承認の明細（<see cref="Counts"/> と同じ母集合・承認時刻の順）。
    /// 件数だけで作った値（旧い呼び出し）では空である。
    /// </summary>
    public IReadOnlyList<StopLossMethodApproval> Approvals { get; init; } = [];

    /// <summary>新規建ての承認の総数（手法を問わない）。</summary>
    public int TotalApprovals => Counts.Sum(c => c.Count);

    public static StopLossMethodUsage From(IEnumerable<OrderApproved> approvals, int unreadableCount = 0)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        var seen = new HashSet<Guid>();
        var byMethod = new Dictionary<StopLossExecutionMethod, int>();
        var counted = new List<StopLossMethodApproval>();
        foreach (var a in approvals)
        {
            if (a.Intent.PositionEffect != PositionEffect.Open)
                continue;
            if (!seen.Add(a.DecisionId))
                continue;

            byMethod[a.StopLossMethod] = byMethod.GetValueOrDefault(a.StopLossMethod) + 1;
            counted.Add(new StopLossMethodApproval(a.DecisionId, a.StopLossMethod, a.ApprovedAt));
        }

        var counts = byMethod
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => new StopLossMethodCount(kv.Key, kv.Value))
            .ToList();
        return new StopLossMethodUsage(counts, unreadableCount)
        {
            Approvals = [.. counted.OrderBy(a => a.ApprovedAt)],
        };
    }

    /// <summary>
    /// 日報に出す手法の表示名（計画 ADR-0040 決定1 の表の「ID ＋ 手法」）。画面（SC-02 / SC-03）の表示名と同じ語を使う。
    /// 未知の値は <c>不明(N)</c>（画面の安全側フォールバックと同じ形）。
    /// </summary>
    public static string Label(StopLossExecutionMethod method) => method switch
    {
        StopLossExecutionMethod.BrokerStopOrder => "S0 ブローカー側逆指値",
        StopLossExecutionMethod.SoftwareStop => "S1 ソフトウェア逆指値",
        StopLossExecutionMethod.NoProtectiveStop => "S2 逆指値なしの建玉を許容",
        StopLossExecutionMethod.AlternativeBrokerOrderType => "S3 他のブローカー側注文種別",
        _ => $"不明({(int)method})",
    };
}

/// <summary>手法 1 つぶんの新規建ての承認件数。</summary>
public sealed record StopLossMethodCount(StopLossExecutionMethod Method, int Count);

/// <summary>#1002, IADR-0429 決定3: 数えた承認 1 件（新規建て・DecisionId で重複を除いたもの）。</summary>
public sealed record StopLossMethodApproval(Guid DecisionId, StopLossExecutionMethod Method, DateTimeOffset ApprovedAt);
