using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetEntryBlockers;

// FR-10, FR-04, #1113, IADR-0463 決定 3: 銘柄単位の「新規建ての可否」。取引判断が LLM を呼ぶ前に読む。
// LongSide は買いの新規建て（ロングを建てる）、ShortSide は売りの新規建て（ショートを建てる）について、
// **状態から確定する**拒否理由（審査と同じ述語。EntryStateBlockers）。空＝確定する拒否は無い（審査で通るとは限らない）。
// 🔴 不明の状態は理由として返さない（空に見える）。空を「通る」と読まず、LLM を呼ぶ側へ倒すのは受け手の規律である。
public sealed record EntryBlockersView(
    string Symbol,
    Market Market,
    IReadOnlyList<RejectionReason> LongSide,
    IReadOnlyList<RejectionReason> ShortSide);
