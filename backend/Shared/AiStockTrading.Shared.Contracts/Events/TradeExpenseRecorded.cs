using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-11, UC-07, ADR-0016 決定15, #339: **取引記録の経費 1 行を計上した。**
//
// ADR-0016 決定15 は「取引記録に経費区分を持たせ、**建玉単位で紐づけられること**」を要件とし、
// 「集計は後から作れても**記録は遡って復元できない**」ことを理由に挙げる。
// 本イベントが経費台帳への追記そのものであり、監査台帳（イベント全量を JSON で 7 年保持・NFR-10）が
// その保存先である。**専用の永続テーブルは作らない** —— 同じ事実の権威が 2 つになるためである。
//
// 🔴 **`DividendInLieu` は配当の受取ではない。** 空売り建玉の保有者が支払う配当相当額であり、
// 税務上は譲渡費用に近い扱いである。区分を分けずに記録すると**後から区別できない**
// （ADR-0016 決定15 が要点として名指しした点である）。
public record TradeExpenseRecorded(TradeExpense Expense);
