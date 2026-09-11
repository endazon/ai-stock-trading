using System.Globalization;
using Microsoft.Extensions.Configuration;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-15, ADR-0033 決定5, ADR-0037 決定3, #750, IADR-0254（2026-09-11 追記）:
// 見積り承認額を構成 `Stage0Recording:ApprovedEstimateJpy` から読む（記録側と**同じセクション名・同じキー名**）。
//
// 🔴 **キー名を揃えるのは、2 サービスで別名にすると同値であるべき値だと読めなくなるためである。**
// 値そのものは 2 箇所に載る（記録側＝取引判断サービス／報告側＝本サービス）。**片方だけ変えると対比が黙って誤る**——
// 検知する機械は無いので、helm の両方の設定点に「必ず同値にする」注記を置いて人手で守る
// （`Stage0Recording__LlmTrainingCutoff` が既に同じ扱いである）。
//
// 🔴 **未設定・空・解釈不能・負値はすべて null（未供給）へ倒す。0 円へ倒さない。**
// 承認額 0 円は「0 円で承認された」であり、未設定は「承認されていない」である。
public sealed class ConfigurationStage0RecordingEstimateSource(IConfiguration configuration)
    : IStage0RecordingEstimateSource
{
    /// <summary>構成キー（記録側 <c>Stage0RecordingOptions</c> のセクション名・プロパティ名と一致させる）。</summary>
    public const string ConfigurationKey = "Stage0Recording:ApprovedEstimateJpy";

    public decimal? GetApprovedEstimateJpy()
    {
        var raw = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;

        // 負の承認額は構成の誤りである。**0 円へ丸めない**——誤った値を正しい値のように出さない。
        return value < 0m ? null : value;
    }
}
