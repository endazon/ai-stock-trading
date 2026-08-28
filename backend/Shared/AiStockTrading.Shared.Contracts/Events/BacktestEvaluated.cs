namespace AiStockTrading.Shared.Contracts.Events;

// FR-15, FR-20, FR-11, UC-06, ADR-0008, IADR-0089: バックテスト（Stage 0 合格判定・BacktestService/#16）の verdict。
// Risk が購読して段階別実績ストア（IStagePerformanceStore）へ射影し、Stage 0→1 昇格ゲートの入力
// （BacktestPassed / BacktestMaxDrawdownRatio）を解錠する（#164）。監査サービスも購読して中央監査台帳へ集約する（FR-11）。
// verdict・メトリクスは BacktestService の純ドメイン（Stage0Decision）で判定・算出され、本契約には primitive で渡す。
// 段階/enum に依存しないよう BacktestService.Domain / RiskManagementService.Domain へ依存させない（依存逆転を避ける・IADR-0082 と同型）。
public record BacktestEvaluated(
    bool Passed,
    decimal MaxDrawdownRatio,
    double DeflatedSharpe,
    double ProbabilityOfBacktestOverfitting,
    string FailedChecks,
    DateTimeOffset EvaluatedAt);
