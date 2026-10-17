namespace AiStockTrading.Shared.Contracts.Trading;

// FR-04, FR-07, FR-11, ADR-0068 決定4, #1373, IADR-0548 決定5: 判断が参照した確定済み日報の方針の出所。
// PeriodKey は日報の期間キー（例 daily-2026-10-17）、Version はその確定版の版番号（報告書サービスの確定後の行の版）。
// 1 日の中で同じ期間キーの版が足されて再確定され得る（ADR-0068 決定 1）ため、監査で「どの判断がどの版の方針で行われたか」を
// 追えるように判断の記録へ載せる。
public sealed record DailyPolicyReference(string PeriodKey, int Version);
