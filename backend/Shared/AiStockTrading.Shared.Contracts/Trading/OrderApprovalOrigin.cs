namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定1: 承認（<c>OrderApproved</c>）の<b>出どころ</b> ——「誰がこの注文を求めたか」。
/// <para>
/// 発注執行は発注の記録（<c>executed_orders.ApprovalOrigin</c>）へ写し、ソフトウェア逆指値（S1）の決済の前の取消が
/// 判断の手仕舞い（取り消す）と、利用者の手仕舞い・維持率割れの自動縮小（取り消さず差し引く）を見分けるために読む。
/// </para>
/// <para>
/// 🔴 <b><see cref="Unknown"/> は「分からない」であり、「判断ではない」ではない。</b>本項目を持たない旧いメッセージ・書き手が渡し忘れた承認は
/// <see cref="Unknown"/> として読まれ、S1 は判断の手仕舞いと同じく取り消す側へ倒す（損切りを止めない。FR-10）。
/// </para>
/// <para>
/// 🔴 <b>序数を書き換えない。</b>整数として永続化するため、既存メンバの間へ挿入すると過去の記録の意味が変わる。
/// 追加は常に末尾へ行う（<c>OrderApprovalOriginContractTests</c> が全メンバの序数を表で固定する）。
/// </para>
/// </summary>
public enum OrderApprovalOrigin
{
    /// <summary>分からない（既定。旧いメッセージ・書き手の渡し忘れ）。</summary>
    Unknown = 0,

    /// <summary>発注前審査が取引判断（<c>TradeDecisionMade</c>）を承認したもの（決済なら判断の手仕舞い・利確）。</summary>
    TradeDecision = 1,

    /// <summary>利用者（owner）の手仕舞い要求（UC-06・<c>PositionCloseService</c>）。</summary>
    OwnerClose = 2,

    /// <summary>維持率割れの自動縮小（UC-06・<c>MaintenanceMarginReductionService</c>）。</summary>
    MaintenanceMarginReduction = 3,
}
