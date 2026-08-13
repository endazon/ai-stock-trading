namespace AiStockTrading.Shared.Contracts.Events;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2, IADR-0182:
// GFV 違反による停止が**利用者の明示的な操作で解除された**。
//
// ADR-0028 決定2 は「**誰が・いつ・どの記録に対して**解除したかを監査ログ（FR-11）へ記録する」と定める。
// 監査サービスが購読して中央監査台帳（audit_events）へ集約する。
//
// 🔴 **これは「違反記録が消えた」ことを意味しない。** 決定1 が「違反記録は失効させない」と定めており、
// 台帳の行は残り続ける。本イベントが表すのは**停止の解除**である。
public record GoodFaithViolationsCleared(
    // 解除した利用者（認証済みトークンの名前）。
    string ClearedBy,
    // 解除の理由（原因の是正が済んでいることの確認の記録）。
    string Reason,
    // **どの記録に対して**解除したか（違反記録の計上単位＝ブローカの注文 ID）。
    IReadOnlyList<string> ClearedOrderIds,
    // 解除後になお残る違反件数。**0 とは限らない**——解除の最中に新たな違反が計上され得る。
    // 「解除したのに止まったまま」を監査から説明できるようにするために載せる。
    int RemainingCount,
    DateTimeOffset ClearedAt);
