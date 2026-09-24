namespace RiskManagementService.Features.RiskManagement;

/// <summary>
/// FR-10, #935, IADR-0394 決定1/6: 取引台帳の承認行が<b>どの経路から書かれたか</b>（承認行の由来）。
/// 決済（Close）が<b>損切り</b>だったかを後から見分けるために承認行へ永続化する（<c>approved_orders.Source</c>）。
/// <para>
/// 書き手は 4 経路で、それぞれが自分の値を<b>明示して</b>渡す（<c>IPortfolioLedgerStore.AppendApproval</c>）。
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
    /// <c>OrderApproved</c> を購読して書いた承認（<c>OrderApprovedLedgerHandler</c>）。
    /// 発行元は発注前審査（判断由来の新規建て・決済）・owner の手仕舞い・維持率割れの自動縮小の 3 つであり、
    /// <b>いずれも損切りではない</b>。
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
}
