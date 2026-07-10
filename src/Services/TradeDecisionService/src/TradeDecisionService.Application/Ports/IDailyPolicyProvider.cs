namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-04, FR-07: 確定済み日報の方針。確定前・未確定なら null を返し、判断サービスは取引しない（FR-07）。
// 実データは報告書サービス（#14）連携で供給する。Summary は LLM プロンプトに渡す方針要約。
public interface IDailyPolicyProvider
{
    DailyPolicy? GetCurrent();
}

// 確定済み日報の方針（当日）。Summary は当日の売買方針の要約。
public record DailyPolicy(DateOnly Date, string Summary);
