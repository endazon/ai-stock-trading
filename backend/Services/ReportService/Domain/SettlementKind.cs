namespace ReportService.Domain;

/// <summary>
/// FR-06, FR-16, FR-10, #1357, IADR-0540 決定 1: 約定が属する<b>承認行の由来</b>（リスク管理の取引台帳 <c>approved_orders.Source</c> の写し）。
/// <para>
/// 🔴 <b>序数はリスク管理の <c>ApprovalSource</c> と同じ</b>（REST は数値で往復する）。<b>書き換えない・末尾へだけ足す</b>
/// （<c>FillApprovalSourceTests</c> が全メンバの序数を表で固定する。送り手側は <c>ApprovalSourceTests</c>）。
/// 型名を送り手と変えてあるのは、送り手の型による契約テスト（IADR-0420）の判定を効かせるためである。
/// </para>
/// </summary>
public enum FillApprovalSource
{
    /// <summary>判断を経ない承認（利用者の手仕舞い・維持率割れの自動縮小）。<b>2026-10-07（#1176）より前は判断由来もこの値</b>。</summary>
    OrderApproved = 0,

    /// <summary>S0: ブローカー側の保護逆指値の決済レグ（約定が付いた＝損切りの成立）。</summary>
    ProtectiveStopS0 = 1,

    /// <summary>S1: ソフトウェア逆指値が損切りラインへ到達して発注した成行決済。</summary>
    SoftwareStopS1 = 2,

    /// <summary>保護逆指値が成立しないときの成行手仕舞い（損切りとは数えない）。</summary>
    ProtectionLostClose = 3,

    /// <summary>発注前審査が取引判断を承認したもの（決済なら判断由来の決済＝利確・判断の手仕舞い）。</summary>
    TradeDecision = 4,
}

/// <summary>
/// FR-06, FR-16, FR-10, #1357, IADR-0540 決定 2: 散文へ渡す<b>決済の種別</b>。<c>null</c>（<see cref="SettlementKind"/>?）＝<b>種別未供給</b>。
/// </summary>
public enum SettlementKind
{
    /// <summary>損切り（保護逆指値 S0 の約定・ソフトウェア逆指値 S1 の決済）。</summary>
    StopLoss,

    /// <summary>取引判断による決済（利確・方針による撤退・判断の手仕舞い。<b>どれかは判断根拠の記録に依る</b>）。</summary>
    DecisionExit,

    /// <summary>判断を経ない決済（利用者の手仕舞い・維持率割れの自動縮小のいずれか。<b>両者は区別できない</b>）。</summary>
    NonDecisionClose,

    /// <summary>保護逆指値が成立しないときの成行手仕舞い（損切りではない）。</summary>
    ProtectionLostClose,
}

/// <summary>FR-06, FR-16, #1357, IADR-0540 決定 2: 承認行の由来と判断の記録の有無から決済の種別を決める（純関数）。</summary>
public static class SettlementKinds
{
    /// <summary>
    /// 決済の種別。<paramref name="decisionRecord"/> は判断の記録（<c>TradeDecisionMade</c>）が<b>ある</b>（true）・<b>無い</b>（false）・
    /// <b>判断の記録そのものが未供給</b>（null）。<c>null</c> を返したら種別未供給（推定で埋めない）。
    /// </summary>
    public static SettlementKind? Of(FillApprovalSource? source, bool? decisionRecord) => source switch
    {
        FillApprovalSource.ProtectiveStopS0 or FillApprovalSource.SoftwareStopS1 => SettlementKind.StopLoss,
        FillApprovalSource.TradeDecision => SettlementKind.DecisionExit,
        FillApprovalSource.ProtectionLostClose => SettlementKind.ProtectionLostClose,
        // 🔴 #1176 より前の判断由来の決済もこの値で書かれている。判断の記録は取引判断サービスだけが残す
        // （利用者の手仕舞い・自動縮小は残さない）ので、記録の有無で分ける。記録が未供給なら分けない。
        FillApprovalSource.OrderApproved => decisionRecord switch
        {
            true => SettlementKind.DecisionExit,
            false => SettlementKind.NonDecisionClose,
            null => null,
        },
        _ => null,
    };

    /// <summary>散文へ渡す種別の語（未供給も 1 つの語で表す）。</summary>
    public static string Label(SettlementKind? kind) => kind switch
    {
        SettlementKind.StopLoss => "損切り",
        SettlementKind.DecisionExit => "取引判断による決済",
        SettlementKind.NonDecisionClose => "判断を経ない決済（利用者の手仕舞い又は維持率割れの自動縮小）",
        SettlementKind.ProtectionLostClose => "保護逆指値が成立しないときの手仕舞い",
        _ => UnsuppliedLabel,
    };

    /// <summary>種別を取れなかった決済の語。</summary>
    public const string UnsuppliedLabel = "種別未供給";
}
