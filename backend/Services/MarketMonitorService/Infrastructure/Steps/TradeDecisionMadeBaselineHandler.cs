using MarketMonitorService.Features.MarketMonitor;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace MarketMonitorService.Infrastructure.Steps;

// FR-03, UC-02: 取引判断の確定（TradeDecisionMade）を購読し、対象銘柄の基準値を「判断時点価格」へ更新する。
// これにより変動率が「前回 AI 判断時点価格比」で計算される（前日終値ではない。04_workflows/02）。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<TradeDecisionMade> から Wolverine のハンドラへ移行した。
// **キュー名の分離の根拠が変わった**（重要）:
//   旧（IADR-0106・#258 の対策）: MassTransit の既定キュー名は consumer クラス名のみから導かれるため、
//     RiskManagementService の TradeDecisionMadeConsumer と同名だと同一キューを共有して competing consumer になる。
//     ゆえにクラス名へ `Baseline` を入れること自体が機能要件だった。
//   新（IADR-0129 決定 1）: Wolverine はキュー名の導出にハンドラのクラス名を**一切**使わない。分離は
//     `<ServiceName>.<メッセージ型名>`（= ai-stock-trading.market-monitor-service.TradeDecisionMade）が担う。
//     よって `Baseline` は**関心事を表す読みやすさのための命名**に戻り、機能要件ではなくなった
//     （名前を変えてもキューは変わらない＝#258 が名前で再発することも無い）。
//
// IADR-0129 決定 9: Wolverine はハンドラ型が public でなければ受け付けない（明示指定しても
// 「Handler types must be public, concrete, and closed」で拒否される）。よって public sealed とする。
public sealed class TradeDecisionMadeBaselineHandler(
    IPriceBaselineStore baselineStore,
    ILogger<TradeDecisionMadeBaselineHandler> logger)
{
    public void Handle(TradeDecisionMade message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var intent = message.Intent;
        baselineStore.SetBaseline(intent.Symbol, intent.Market, intent.Price);
        logger.LogInformation(
            "基準値を更新: {Symbol}/{Market} = {Price}", intent.Symbol, intent.Market, intent.Price);
    }
}
