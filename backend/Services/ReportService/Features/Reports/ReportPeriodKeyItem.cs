namespace ReportService.Features.Reports;

// FR-14, FR-07, UC-03〜05, #843 項目1, IADR-0418: 会話キーの一覧（入力補完の候補）の 1 行。
// **会話キーと並び替えに使う開始日だけ**を持つ射影で、本文・要約・状態は載せない（IADR-0240 決定4/5 と同じ射影）。
public sealed record ReportPeriodKeyItem(string PeriodKey, DateOnly PeriodStart);
