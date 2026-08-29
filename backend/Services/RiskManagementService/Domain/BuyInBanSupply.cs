namespace RiskManagementService.Domain;

// FR-10, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 決定5:
// 強制買戻し由来の 30 日空売り禁止だけを、**空売り文脈（ShortSellOrderContext）とは独立に**判定コアへ供給する入力。
//
// **なぜ文脈に相乗りしないのか**: `ShortSellOrderContext` は借株可否・借株料・維持率・エクスポージャ・権利確定日を
// 要求するが、**借株照会の供給元が無いため今も組めない**（IADR-0158 / blocked-tasks）。組めないからといって
// 維持率 `null`・エクスポージャ `0` で埋めた偽の文脈を作れば、**「観測した結果ゼロだった」と読まれる値を発明する**
// ことになる。供給できる値だけを供給する。
//
// 判定式そのものは <see cref="BuyInBanPolicy.IsBanned"/> が単一情報源であり、文脈経由の判定
// （<c>ShortSellEvaluator</c>）と共有する。
public sealed record BuyInBanSupply(DateOnly Today, DateOnly? BanUntil);
