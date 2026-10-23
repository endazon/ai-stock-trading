namespace ReportService.Features.Reports;

// FR-07, ADR-0003: 確定済み日報の方針。取引判断（#11）の IDailyPolicyProvider の実データ源（結線は後続 #22）。
// FR-04, FR-11, ADR-0068 決定4, #1373, IADR-0548 決定5: PeriodKey・Version は、この方針を持つ確定版の期間キーと版番号
// （確定後の行の版）。取引判断が判断の記録へ残す（末尾へ足した＝非破壊。受け手は欠けていても方針を使う）。
// 🔴 FR-07, #1423 M-6, ADR-0075 決定1〜3, IADR-0583 決定3: NoResponseHalted は「無応答時の停止」が効いている印
// （設定が停止で、より新しい日報が承認待ちのまま期限〔翌営業日の開場〕を過ぎた）。**方針そのものは直近の確定済み日報のまま返す**
// （閉場前の手仕舞いが「方針を読めない」へ倒れないように）。取引判断はこの印で判断の全体を見送る。確定で消える。末尾・既定 false（非破壊）。
public sealed record ConfirmedDailyPolicy(
    DateOnly Date, string Summary, int AssumptionsVersion, string PeriodKey, int Version, bool NoResponseHalted = false);

// FR-15, FR-07, #1308, IADR-0528 決定3: ある時点で確定していた日報の方針（`GET /reports/daily-policy/as-of`）。
// ConfirmedAt は確定の時刻（照会の時点以前であることを受け手が確かめられるように返す）。
// ADR-0068 決定3・4, #1373, IADR-0548: ConfirmedAt は選んだ<b>版</b>の確定時刻。PeriodKey・Version はその版。
public sealed record ConfirmedDailyPolicyAsOf(
    DateOnly Date, string Summary, int AssumptionsVersion, DateTimeOffset ConfirmedAt, string PeriodKey, int Version);

// 確定操作の結果。Transitioned=true は Draft→Confirmed の遷移が起きたこと（イベント発行対象）。
// FR-07, FR-13, #1025, IADR-0433 決定 7（PR #1027 の監査 H1）: Version は確定後の行の版（確定は版を 1 進める）。
// 冪等な再確定（Transitioned=false）では、**どの版で確定されたか**を呼び出し側が見分けるために使う
// （確定された下書きの版＝Version − 1。VersionedReport.IsConfirmedAtDraftVersion）。
// FR-07, ADR-0068 決定1・6, #1373, IADR-0549 決定2: SupersededVersion は、この確定が効いている日報の<b>再確定</b>（版 n+1 の確定）だったとき、
// それまで効いていた同じ期間キーの確定版の版番号（通知で「効いている版が変わった」と書くため）。初回の確定・冪等な再確定は null。
public sealed record ConfirmResult(
    ReportService.Domain.TradingReport Report, bool Transitioned, int Version = 0, int? SupersededVersion = null);
