namespace AiStockTrading.Shared.Contracts.Llm;

// NFR-05, #724, IADR-0323: LLM ゲートウェイ（MSP のサービス）を叩くための s2s 認証の設定点。
//
// 呼び出す 2 サービス（TradeDecisionService / ReportService）が同じ文字列を別々に書くと、
// 片方のタイプミスが「トークンが付かないだけ」＝**静かな 401** になって現れる。両者が引く 1 箇所に置く。
//
// セクションは KB（`KnowledgeBase:Auth`・IADR-0093）と**分ける**。同じ MSP レルムの資格情報を与える運用でも、
// 将来 LLM 専用の confidential client へ分けるときに values の 2 行の差し替えだけで済む（コード変更不要）。
public static class LlmGatewayAuth
{
    /// <summary>認証設定のセクション名。AST レルムの <c>ServiceAuth</c> とは別物（MSP レルムを指す）。</summary>
    public const string SectionName = "LlmGateway:Auth";

    /// <summary>token 取得専用の名前付き HttpClient 名（付与の鎖を通さない＝自己再帰の回避）。</summary>
    public const string TokenClientName = "llm-gateway-token";
}
