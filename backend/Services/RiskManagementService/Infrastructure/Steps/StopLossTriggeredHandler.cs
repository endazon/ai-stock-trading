using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// FR-10, FR-03, UC-02, #331, IADR-0210 決定5: 市場監視の損切りイベント（StopLossTriggered）を購読し、
// **検知の記録（ログ）のみ**を行う。損切りの実行はブローカー側の逆指値が担い（planning#88 裁定・
// 逆指値一本化）、**本ハンドラは決済注文（OrderApproved）を発行しない**——同一の損切りがシステム側と
// ブローカー側の 2 経路から発注されると二重決済となり、決済後に残る注文が反対方向の建玉を生むためである
// （業務フロー 02。旧実装〔IADR-0015・StopLossExecutionService〕は Close 承認を発行していた）。
//
// 永続記録は監査サービス（FR-11）、Discord 通知（FR-09）は通知サービスが同イベントを独立に購読して担う。
public sealed class StopLossTriggeredHandler(ILogger<StopLossTriggeredHandler> logger)
{
    public void Handle(StopLossTriggered message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 到達の検知をサービスログにも残す（ここでは何も発注しない）。
        // FR-10, ADR-0040 決定1, #820（#826 項目 2）, IADR-0344 決定7: 決済するかは建玉ごとの手法で決まり、本サービスは知らない
        // （S0＝ブローカー側の逆指値・S1＝発注執行が成行決済・S2＝誰も決済しない・S3＝ブローカー側の代替注文〔#826 項目 2 の残余, IADR-0347〕）。
        // 断定せず列挙する。
        logger.LogWarning(
            "損切りライン到達を検知: {Symbol}/{Market} 建玉方向={PositionSide} 数量={Quantity}"
                + " 損切り価格={StopLossPrice} 現在値={Price}（リスク管理は発注しない。決済は手法による:"
                + " S0=ブローカー側の逆指値／S1=発注執行が成行決済／S2=誰も決済しない／S3=ブローカー側の代替注文）",
            message.Symbol, message.Market, message.PositionSide, message.Quantity,
            message.StopLossPrice, message.Price);
    }
}
