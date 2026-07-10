using System.Globalization;
using System.Text;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.TradeDecision.Application.Services;

// FR-04, ADR-0003: 確定済み日報の方針・価格変動トリガー・サイジング文脈から LLM プロンプトを構築する。
// AI は「確定済み日報の方針とリスク制約の範囲内でのみ」判断する（ADR-0003）。出力は JSON 構造化を要求する。
public static class TradeDecisionPromptBuilder
{
    public static string Build(PriceMovementDetected trigger, DailyPolicy policy, SizingContext context)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("あなたは確定済み日報の方針とリスク制約の範囲内でのみ判断する取引アシスタントです。");
        sb.AppendLine("方針の範囲外・不確実な場合は必ず Hold（取引しない）を選びます。");
        sb.AppendLine();
        sb.AppendLine($"# 確定済み日報の方針（{policy.Date:yyyy-MM-dd}）");
        sb.AppendLine(policy.Summary);
        sb.AppendLine();
        sb.AppendLine("# 価格変動トリガー");
        sb.AppendLine($"- 銘柄: {trigger.Symbol} / 市場: {trigger.Market}");
        sb.AppendLine($"- 現在値: {trigger.Price.ToString(ci)} / 基準値: {trigger.BaselinePrice.ToString(ci)} / 変動率: {trigger.ChangeRatio.ToString("P2", ci)}");
        sb.AppendLine();
        sb.AppendLine("# リスク制約");
        sb.AppendLine($"- 運用資金: {context.Capital.ToString(ci)} 円 / 1取引リスク: {context.Limits.PerTradeRiskRatio.ToString("P1", ci)}");
        sb.AppendLine($"- 1注文金額上限: {context.Limits.MaxOrderAmount.ToString(ci)} 円 / 段階残枠: {context.StageCapitalRemaining.ToString(ci)} / 当日発注残枠: {context.DailyOrderRemaining.ToString(ci)}");
        sb.AppendLine();
        sb.AppendLine("# 出力形式（JSON のみ）");
        sb.AppendLine("{\"action\":\"Buy|Sell|Hold\",\"rationale\":\"判断根拠\",\"referencePrice\":参照価格,\"stopLossDistancePerShare\":損切り幅}");
        return sb.ToString();
    }
}
