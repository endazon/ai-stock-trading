namespace RiskManagementService.Features.RiskManagement;

/// <summary>
/// FR-10, #935, IADR-0394 決定1/6: 取引台帳の承認行が<b>どの経路から書かれたか</b>（承認行の由来）。
/// 決済（Close）が<b>損切り</b>だったかを後から見分けるために承認行へ永続化する（<c>approved_orders.Source</c>）。
/// <para>
/// 書き手は 4 経路（#1176 以後、<c>OrderApproved</c> の経路は判断由来か否かで 2 値を書き分ける）で、それぞれが自分の値を<b>明示して</b>渡す（<c>IPortfolioLedgerStore.AppendApproval</c>）。
/// 🔴 <b>値が無い（<c>null</c>）行は「由来が記録されていない」＝不明である</b>——本列を足す前に記録された行、
/// または由来を渡し忘れた書き手。<b>「損切りではない」として扱ってはならない</b>（決定6）。
/// </para>
/// <para>
/// 🔴 <b>序数を書き換えない。</b>整数として永続化するため、既存メンバの間へ挿入すると過去の行の意味が変わる。
/// 追加は常に末尾へ行う（<c>ApprovalSourceTests</c> が全メンバの序数を表で固定する）。
/// </para>
/// </summary>
public enum ApprovalSource
{
    /// <summary>
    /// <c>OrderApproved</c> を購読して書いた承認（<c>OrderApprovedLedgerHandler</c>）のうち、<b>判断を経ないもの</b>
    /// （owner の手仕舞い・維持率割れの自動縮小）。<b>いずれも損切りではない</b>。
    /// <para>
    /// ［2026-10-07 / #1176・IADR-0495 決定4］判断由来の承認（発注前審査が取引判断を承認したもの）は <see cref="TradeDecision"/> で書く。
    /// <b>それより前に書かれた行は判断由来もこの値</b>であり、owner の手仕舞い・自動縮小と区別できない。
    /// </para>
    /// </summary>
    OrderApproved = 0,

    /// <summary>
    /// <b>S0</b>: ブローカー側の保護逆指値の決済レグ（<c>ProtectiveStopPlaced</c>）。承認は<b>武装した時点</b>であり、
    /// 損切りが成立したのは<b>このレグに約定が付いたとき</b>である。
    /// </summary>
    ProtectiveStopS0 = 1,

    /// <summary>
    /// <b>S1</b>: ソフトウェア逆指値が損切りラインへ到達して発注した成行決済（<c>SoftwareStopExecuted</c> /
    /// <c>ClosePlaced</c>）。承認は<b>発動した時点</b>である。
    /// </summary>
    SoftwareStopS1 = 2,

    /// <summary>
    /// 保護逆指値が成立しないときの成行手仕舞い（<c>ProtectiveStopCoverageLost</c>）。
    /// 価格が損切りラインへ達したのではなく、保護の維持に失敗した対処であり、<b>損切りとは数えない</b>。
    /// </summary>
    ProtectionLostClose = 3,

    /// <summary>
    /// FR-10, #1176, IADR-0495 決定4: <c>OrderApproved</c> のうち<b>発注前審査が取引判断（<c>TradeDecisionMade</c>）を承認したもの</b>
    /// （<c>OrderApproved.FromTradeDecision</c> が true）。決済なら<b>判断由来の決済</b>（利確・判断の手仕舞い）であり、
    /// その取引日のうちは同じ方向の新規建てを止める（<c>DecisionExitProjection</c>）。損切りではない。
    /// </summary>
    TradeDecision = 4,
}
