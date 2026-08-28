namespace TradeDecisionService.Application.Ports;

// FR-04, FR-07, IADR-0028: 確定済み日報の方針。確定前・未確定・依存先障害なら null を返し、判断サービスは取引しない（FR-07・フェイルセーフ）。
// 実データは報告書サービス（#14）の GET /reports/daily-policy を同期照会して供給する（HttpDailyPolicyProvider）。
// 同期 HTTP を sync-over-async にしないため非同期とする。Summary は LLM プロンプトに渡す方針要約。
public interface IDailyPolicyProvider
{
    Task<DailyPolicy?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

// 確定済み日報の方針（当日）。Summary は当日の売買方針の要約。
public record DailyPolicy(DateOnly Date, string Summary);
