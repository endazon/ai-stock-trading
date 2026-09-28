using MarketMonitorService.Features.MarketMonitor;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace MarketMonitorService.Infrastructure.Steps;

// 🔴 UC-02, FR-03, #1077, IADR-0452 決定2: AI 判断後の見送り（TradeDecisionHeld）を購読し、対象銘柄の基準値を
// 「判断時点価格」へ更新する。計画の基準点は「前回 AI 判断を行った時点の価格」であり（04_workflows/02 §補足）、
// Hold も AI 判断である。以前は TradeDecisionMade（発注意図あり）だけが基準値を進めたため、Hold が続く間は基準値が
// 作られず（または古いまま）、UC-02 が一度も発火しなかった（#1077。稼働 PoC 2026-09-28 の実測）。
//
// 判断をしなかった見送り（日報未確定・現在値なし等）では、取引判断はこのイベントを出さない（IADR-0452 決定1）。
// キューは IADR-0129 決定1 の規則で ai-stock-trading.market-monitor-service.TradeDecisionHeld（クラス名は関与しない）。
// IADR-0129 決定 9: Wolverine はハンドラ型が public でなければ受け付けない。よって public sealed とする。
public sealed class TradeDecisionHeldBaselineHandler(
    IPriceBaselineStore baselineStore,
    ILogger<TradeDecisionHeldBaselineHandler> logger)
{
    public void Handle(TradeDecisionHeld message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 発行側は正の価格しか載せないが、基準値 0 以下は変動率の分母を壊すため受け側でも守る（誤発火より不更新）。
        if (message.Price <= 0m)
        {
            logger.LogWarning(
                "判断後の見送りの価格が正でないため基準値を更新しない: {Symbol}/{Market} = {Price}",
                message.Symbol, message.Market, message.Price);
            return;
        }

        baselineStore.SetBaseline(message.Symbol, message.Market, message.Price);
        logger.LogInformation(
            "基準値を更新（判断後の見送り・{Reason}）: {Symbol}/{Market} = {Price}",
            message.Reason, message.Symbol, message.Market, message.Price);
    }
}
