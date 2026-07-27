using System.Globalization;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;
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
    // FR-17, IADR-0076 決定5: includeProfitability=false（既定）なら採算節・expectedProfitPerShare を出さない＝
    // 採算ゲート無効時（既定）はプロンプト文言も現行動作と完全に一致させる（LLM の判断傾向も変えない）。有効時のみ注入する。
    // FR-02, IADR-0099 決定2: currentPrice（権威ある現在値）は定時（Scheduled）トリガーの価格文脈を補う。非 null のとき
    // だけ定時節に「現在値」行を追記する（既定 null＝現行動作＝価格行なし）。価格変動（PriceMovement）節は既に trigger.Price
    // を出しているため currentPrice の有無で変えない（現在値供給の有無で PriceMovement 経路のプロンプト文言を変えない）。
    public static string Build(
        DecisionTrigger trigger, DailyPolicy policy, SizingContext context,
        IReadOnlyList<RetrievedContext>? retrieved = null,
        bool includeProfitability = false,
        decimal? currentPrice = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        var ci = CultureInfo.InvariantCulture;
        // FR-10, FR-17, #257, IADR-0106: 価格は銘柄の市場の通貨、リスク制約は基準通貨（円）である。両者が異なる市場では
        // 単位を明示しないと LLM が外貨建ての価格を円と解釈する（実測: 336.77 USD を「購入額 336.77 円」と解釈）。
        // 基準通貨の市場（日本株）では単位注記を出さず、従来のプロンプト文言と一致させる。
        var currency = MarketCurrency.Of(trigger.Market);
        var priceUnit = currency == MarketCurrency.Base ? string.Empty : $" {currency.ToString().ToUpperInvariant()}";
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
            sb.AppendLine($"- 現在値: {price.ToString(ci)}{priceUnit} / 基準値: {trigger.BaselinePrice?.ToString(ci)}{priceUnit} / 変動率: {trigger.ChangeRatio?.ToString("P2", ci)}");
        }
        else
        {
            sb.AppendLine("# 定時サイクル（価格変動トリガーなし）");
            sb.AppendLine($"- 銘柄: {trigger.Symbol} / 市場: {trigger.Market}");
            // FR-02, IADR-0099 決定2: 権威ある現在値があれば価格文脈として載せる（定時トリガーは価格を持たないため
            // これが無いと LLM は Buy/Sell の根拠を持てず常に Hold に倒れる）。null（既定）なら行を出さず現行動作。
            if (currentPrice is { } cp)
            {
                sb.AppendLine($"- 現在値: {cp.ToString(ci)}{priceUnit}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("# リスク制約");
        sb.AppendLine($"- 運用資金: {context.Capital.ToString(ci)} 円 / 1取引リスク: {context.Limits.PerTradeRiskRatio.ToString("P1", ci)}");
        sb.AppendLine($"- 1注文金額上限: {context.Limits.MaxOrderAmount.ToString(ci)} 円 / 段階残枠: {context.StageCapitalRemaining.ToString(ci)} / 当日発注残枠: {context.DailyOrderRemaining.ToString(ci)}");
        if (priceUnit.Length > 0)
        {
            // #257, IADR-0106: 円建ての上限と外貨建ての価格が混在することを明示し、回答の単位も固定する
            // （円換算はコード側で行う。LLM に為替計算をさせない＝ADR-0003）。
            sb.AppendLine(
                $"- 上記のリスク制約は円建てです。価格・損切り幅・想定利益は{priceUnit.Trim()}建てで回答します（円換算はシステムが行います）。");
        }

        sb.AppendLine();
        // FR-08, IADR-0072 決定2/3: RAG（#18）で引いた参考情報。非空のときのみ本判断プロンプトに追記する（一次スクリーニングには載せない）。
        AppendRetrievalSection(sb, retrieved);
        // FR-17, 05_trading-assumptions §4, IADR-0076 決定5: 採算ゲート有効時のみ、概算費用（手数料・スプレッド）を控除した採算で
        // 判断させる文脈を注入する。想定利益は費用控除前の 1 株あたり見込み値幅を数値で示させ、費用が相対的に大きい小口取引は Hold を促す。
        // 数値の採算判定はコード側（ProfitabilityGate）で行う。無効時（既定）は本節・フィールドを出さず現行動作のプロンプトと一致させる。
        if (includeProfitability)
        {
            sb.AppendLine("# 採算評価（費用控除後の期待利益）");
            sb.AppendLine("往復の手数料・スプレッド等の費用を差し引いて採算が合う取引のみ選びます。費用が相対的に大きい小口取引は Hold（見送り）とします。");
            sb.AppendLine("expectedProfitPerShare には費用控除前の 1 株あたり想定利益（見込み値幅）を数値で示します。採算が不確実なら Hold を選びます。");
            sb.AppendLine();
        }

        sb.AppendLine("# 出力形式（JSON のみ）");
        sb.AppendLine(includeProfitability
            ? "{\"action\":\"Buy|Sell|Hold\",\"rationale\":\"判断根拠\",\"referencePrice\":参照価格,\"stopLossDistancePerShare\":損切り幅,\"expectedProfitPerShare\":想定利益}"
            : "{\"action\":\"Buy|Sell|Hold\",\"rationale\":\"判断根拠\",\"referencePrice\":参照価格,\"stopLossDistancePerShare\":損切り幅}");
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
