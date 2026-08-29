using System.Globalization;
using System.Text;

namespace ReportService.Domain;

// FR-06, FR-07, FR-16, FR-17, #338, INDEX 決定29, 04_report-templates §目標値・約定外イベントの扱い, IADR-0252:
// 目標値の **YAML ブロック併記**（純関数・決定的）。
//
// 計画の明文（決定29）:
//   「報告書 Markdown 内に、人間可読の表と、AI 判断が機械パースする **YAML ブロック（```yaml フェンス）を併記**する。
//    翌営業日の売買条件（対象・条件・上限）は YAML を正とし、表は表示用とする。
//    `report_type+period` をキーにナレッジベースへ保存し、**取引判断サービスは YAML ブロックのみを読む**
//    （Markdown 表のパースはしない）。」
//
// 🔴 **散文から売買条件を推測して YAML へ書き起こさない。**
// 方針本文（PolicySummary）は利用者が書く自由記述であり、構造化された売買条件の供給は存在しない。
// ここで「読み取れた気がする条件」を機械が書けば、それは**機械が発明した取引条件**である——
// 数値・条件を LLM／コードに発明させない（FR-16）という本サービスの根幹に反する。
// **供給が無いことは `null` と注記で明示する**（既存の未供給表記と同じ規律）。
//
// 🔴 **`status` は確定状態に従う**（#310 の再発防止）。確定済みの報告書が YAML で「未確定」を名乗ると、
// 取引判断サービスが確定済み方針を読み飛ばす（またはドラフトを方針として採る）経路ができる。
public static class ReportPolicyYaml
{
    /// <summary>報告書の機密区分（INDEX 決定43・04_report-templates 共通仕様。全報告書共通の確定値）。</summary>
    public const string Confidentiality = "internal";

    /// <summary>
    /// 方針の機械可読ブロック（```yaml フェンス込み・末尾改行つき）を組み立てる。
    /// </summary>
    public static string Render(ReportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var sb = new StringBuilder();
        sb.Append("```yaml\n");
        sb.Append(CultureInfo.InvariantCulture, $"report_type: {view.Kind.ToString().ToLowerInvariant()}\n");
        sb.Append(CultureInfo.InvariantCulture, $"period: {view.PeriodLabel}\n");
        sb.Append(CultureInfo.InvariantCulture, $"status: {(view.ConfirmedAt is null ? "draft" : "fixed")}\n");
        sb.Append(CultureInfo.InvariantCulture, $"based_on: {view.BasedOn ?? "null"}\n");
        sb.Append(CultureInfo.InvariantCulture, $"assumptions_version: v{view.AssumptionsVersion}\n");
        sb.Append(CultureInfo.InvariantCulture, $"confidentiality: {Confidentiality}\n");

        // 🔴 構造化された売買条件の供給は存在しない。**null と明示し、理由を併記する。**
        // 空の配列（`trading_conditions: []`）にすると「条件が無いことが確定した」と読める——
        // 取引判断サービスは「条件ゼロ＝何も建てない」と読むか「制約なし」と読むかを決められない。
        sb.Append("trading_conditions: null\n");
        sb.Append("trading_conditions_note: >-\n");
        sb.Append("  構造化された売買条件は供給されていない（方針本文は散文である）。\n");
        sb.Append("  未供給であり「条件なし」ではない。散文からの推測でこの欄を埋めてはならない。\n");
        sb.Append("```\n");

        return sb.ToString();
    }
}
