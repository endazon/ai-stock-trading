using ReportService.Domain;

namespace ReportService.Application.Ports;

// FR-06, FR-16, #338, #282, ADR-0017 決定2・決定4, 04_report-templates 月報 §7, IADR-0254:
// 当期間の LLM 利用実績（費用・フォールバック発火・取引判断スキップ）の供給。
//
// 🔴 **権威源は監査台帳である**（イベント全量を JSON で 7 年保持）。費用統制サービスの月次カウンタではない——
// あちらは**上限の対象ぶんしか持たない**ため、報告書生成の費用（#282 で計上点を作った側）が引けない。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。空の記録（事象なし）と混ぜない。**
// 費用 0 円・スキップ 0 件と書けば、計上漏れが正常として読まれる（#282 が実測した形そのものである）。
public interface ILlmUsageRecordSource
{
    /// <summary>JST 取引日 [from, to] の LLM 利用実績。照会できなければ null（未供給）。</summary>
    Task<LlmUsageRecord?> GetUsageAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
}
