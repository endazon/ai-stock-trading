namespace RiskManagementService.Features.RiskManagement.GetTradingHalt;

// 🔴 FR-10, ADR-0009, IADR-0567 決定 2: 新規建てを止める 2 つの利用者の統制（kill switch・一時停止）が**いま成立しているか**だけ。
// 発注執行が新規建てを送る直前に読む（OwnerOrService）。操作者・理由・時刻は返さない（読み手が要らない情報を出さない）。
// 日次損失ロックアウトは含めない（審査が止める。本口の範囲は利用者が手で入れる 2 つの停止）。
public sealed record TradingHaltView(
    bool KillSwitchEngaged,
    bool TradingPaused);
