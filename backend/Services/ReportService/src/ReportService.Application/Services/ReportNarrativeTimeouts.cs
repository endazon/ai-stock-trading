using System.Globalization;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Services;

// FR-06/16, IADR-0123, #308, 04_workflows/03_reporting-cycle:
// 報告書散文 LLM（POST /complete）のタイムアウトを**報告書種別ごとに**解決する純関数。
//
// IADR-0120 / platform#422 で種別ごとに別モデルが割り当たった（日報=sonnet-5 / 週報=opus-5 / 月報=fable-5）結果、
// 所要時間は種別で大きく異なる。サービス共通の 30 秒では週報・月報が構造的に間に合わず、所感が恒常的に
// プレースホルダへ縮退していた（2026-07-31 の経路B live 検証で、要求開始のちょうど 30 秒後に縮退の WRN）。
//
// 解決順は **種別別設定 → 全種別設定（LlmGateway:TimeoutSeconds）→ 組込既定**。
// いずれの段でも空・非数値・0・負値は「未設定」として次段へ倒す（既存 ParseTimeout の fail-safe を踏襲）。
//
// 純関数として Application に置くのは、実 HTTP を伴う Worker 実装（HttpReportNarrativeDrafter）から分離して
// 期待値を単体テストで固定するためである（ReportNarrativePurpose と同じ方針）。
public sealed class ReportNarrativeTimeouts(string? allKinds, string? daily, string? weekly, string? monthly)
{
    // IADR-0123 決定2: 日報は現に成功しているため据置（延ばすと遅延検知が鈍る＝退行を作らない）。
    // 週報・月報は重いモデルが割り当てられており 30 秒では構造的に間に合わないため 120 秒。
    // タイムアウトは異常検知の上限であって所要時間の目標値ではない。
    private static readonly TimeSpan DailyDefault = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeavyDefault = TimeSpan.FromSeconds(120);

    private readonly TimeSpan? _allKinds = Parse(allKinds);
    private readonly TimeSpan? _daily = Parse(daily);
    private readonly TimeSpan? _weekly = Parse(weekly);
    private readonly TimeSpan? _monthly = Parse(monthly);

    /// <summary>報告書種別に対する実効タイムアウト。</summary>
    public TimeSpan For(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => _weekly ?? _allKinds ?? HeavyDefault,
        ReportKind.Monthly => _monthly ?? _allKinds ?? HeavyDefault,
        _ => _daily ?? _allKinds ?? DailyDefault,
    };

    /// <summary>
    /// 全種別の解決値の最大。HttpClient 自体のタイムアウト（要求ごとの打ち切りが壊れても無制限に待たない
    /// 多層防御の上限）に用いる。どの種別の要求もこの値では打ち切られない。
    /// </summary>
    public TimeSpan Max => new[] { For(ReportKind.Daily), For(ReportKind.Weekly), For(ReportKind.Monthly) }.Max();

    // 空・非数値・0・負値は未設定として扱う（null を返して次段へ倒す）。
    private static TimeSpan? Parse(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
