using System.Globalization;
using System.Text;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;

namespace AiStockTrading.TradeDecision.Application.Services;

// FR-02, FR-04, ADR-0003: 確定済み日報の方針・判断トリガー・サイジング文脈から LLM プロンプトを構築する。
// AI は「確定済み日報の方針とリスク制約の範囲内でのみ」判断する（ADR-0003）。出力は JSON 構造化を要求する。
// トリガーは定時（Scheduled）と価格変動（PriceMovement）を合流した DecisionTrigger（IADR-0023）。
public static class TradeDecisionPromptBuilder
{
    // FR-08, IADR-0072 決定3: 参考情報 1 件あたりの本文抜粋の上限。RAG 文脈でプロンプトが過度に膨らむのを防ぐ。
    private const int MaxSnippetChars = 400;

    // retrieved は #18（IADR-0069）の RAG 取得結果（IADR-0072）。null/空は現行動作（参考情報節なし）。
    public static string Build(
        DecisionTrigger trigger, DailyPolicy policy, SizingContext context,
        IReadOnlyList<RetrievedContext>? retrieved = null)
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
        if (trigger.Kind == DecisionTriggerKind.PriceMovement && trigger.Price is { } price)
        {
            sb.AppendLine("# 価格変動トリガー");
            sb.AppendLine($"- 銘柄: {trigger.Symbol} / 市場: {trigger.Market}");
            sb.AppendLine($"- 現在値: {price.ToString(ci)} / 基準値: {trigger.BaselinePrice?.ToString(ci)} / 変動率: {trigger.ChangeRatio?.ToString("P2", ci)}");
        }
        else
        {
            sb.AppendLine("# 定時サイクル（価格変動トリガーなし）");
            sb.AppendLine($"- 銘柄: {trigger.Symbol} / 市場: {trigger.Market}");
        }
        sb.AppendLine();
        sb.AppendLine("# リスク制約");
        sb.AppendLine($"- 運用資金: {context.Capital.ToString(ci)} 円 / 1取引リスク: {context.Limits.PerTradeRiskRatio.ToString("P1", ci)}");
        sb.AppendLine($"- 1注文金額上限: {context.Limits.MaxOrderAmount.ToString(ci)} 円 / 段階残枠: {context.StageCapitalRemaining.ToString(ci)} / 当日発注残枠: {context.DailyOrderRemaining.ToString(ci)}");
        sb.AppendLine();
        // FR-08, IADR-0072 決定2/3: RAG（#18）で引いた参考情報。非空のときのみ本判断プロンプトに追記する（一次スクリーニングには載せない）。
        AppendRetrievalSection(sb, retrieved);
        sb.AppendLine("# 出力形式（JSON のみ）");
        sb.AppendLine("{\"action\":\"Buy|Sell|Hold\",\"rationale\":\"判断根拠\",\"referencePrice\":参照価格,\"stopLossDistancePerShare\":損切り幅}");
        return sb.ToString();
    }

    // FR-04, IADR-0039, L129: 二段判断の一次スクリーニング（軽量モデル・対象銘柄の絞り込み）用プロンプト。
    // 本判断は不要。関心（Buy/Sell 候補か）だけを同一 JSON スキーマで返させ、Parser を共有する。方針外・不確実は Hold。
    public static string BuildScreening(DecisionTrigger trigger, DailyPolicy policy, SizingContext context)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        var sb = new StringBuilder();
        sb.AppendLine("あなたは取引候補の一次スクリーニング担当です。詳細な本判断は行いません。");
        sb.AppendLine("確定済み日報の方針に照らし、この銘柄が本判断に値する取引候補かを絞り込みます。");
        sb.AppendLine("方針の範囲外・関心なし・不確実な場合は必ず Hold（見送り）を選びます。");
        sb.AppendLine();
        sb.AppendLine($"# 確定済み日報の方針（{policy.Date:yyyy-MM-dd}）");
        sb.AppendLine(policy.Summary);
        sb.AppendLine();
        sb.AppendLine($"# 対象: {trigger.Symbol} / 市場: {trigger.Market}");
        sb.AppendLine();
        sb.AppendLine("# 出力形式（JSON のみ・関心の方向のみ）");
        sb.AppendLine("{\"action\":\"Buy|Sell|Hold\",\"rationale\":\"絞り込み理由\",\"referencePrice\":参照価格,\"stopLossDistancePerShare\":損切り幅}");
        return sb.ToString();
    }

    // FR-08, ADR-0003, IADR-0072 決定3: RAG 参考情報節。非空のときのみ出力する（空/null は現行動作を保つため何もしない）。
    // ガードレール（ADR-0003）: 参考情報は「確定日報の方針・リスク制約を上書きしない」旨を明記し、判断の権威順序を保つ。
    private static void AppendRetrievalSection(StringBuilder sb, IReadOnlyList<RetrievedContext>? retrieved)
    {
        if (retrieved is null || retrieved.Count == 0)
            return;

        sb.AppendLine("# 参考情報（ナレッジベース）");
        sb.AppendLine("以下は参考情報であり、確定日報の方針とリスク制約を上書きしません。矛盾・不確実な場合は Hold（取引しない）を選びます。");
        foreach (var hit in retrieved)
        {
            var snippet = Truncate(hit.Text, MaxSnippetChars);
            var source = string.IsNullOrWhiteSpace(hit.SourceUri) ? string.Empty : $"（出典: {hit.SourceUri}）";
            sb.AppendLine($"- [{hit.Title}] {snippet}{source}");
        }

        sb.AppendLine();
    }

    // 本文抜粋を上限文字数で切り詰める（超過時は省略記号を付す）。null/空・上限内はそのまま。
    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text ?? string.Empty;

        return string.Concat(text.AsSpan(0, maxChars), "…");
    }
}
