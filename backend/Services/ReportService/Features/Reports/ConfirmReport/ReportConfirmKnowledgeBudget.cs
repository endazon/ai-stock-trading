using System.Globalization;

namespace ReportService.Features.Reports.ConfirmReport;

// FR-07, FR-08, FR-14, ADR-0068 決定6, #1385, IADR-0556 決定 1: 確定の後の KB の後処理（確定版の写しの保存／置き換え・失効の印・
// ドラフトの写しの削除）にかける時間予算。確定の応答の所要の上限（内側）を決める。
//
// 🔴 **呼び出し側（通知サービス）の確定の上限は、この既定より長く置く**（外側＞内側。通知サービスの
// `ReportsGrpcTransport.DefaultConfirmTimeout` と REST の名前付きクライアント `report-confirm`、既定 30 秒）。
// 通知サービスの試験（T-10-2729）が本ファイルの定数を読んで突き合わせる（サービス間は参照しない）。
public static class ReportConfirmKnowledgeBudget
{
    /// <summary>構成キー（秒・小数可）。未設定・非数値・非正値は既定。</summary>
    internal const string Key = "Reports:Confirm:KnowledgeBudgetSeconds";

    /// <summary>既定の予算（秒）。KB の 1 呼び出しの上限（文書 5 秒・カタログ 30 秒）の連なりを打ち切る。</summary>
    public const int DefaultKnowledgeBudgetSeconds = 20;

    public static TimeSpan Default => TimeSpan.FromSeconds(DefaultKnowledgeBudgetSeconds);

    public static TimeSpan Read(IConfiguration? configuration) => Parse(configuration?[Key]);

    public static TimeSpan Parse(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds) && seconds > 0 && seconds <= int.MaxValue / 1000.0
            ? TimeSpan.FromSeconds(seconds)
            : Default;
}
