using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-20, FR-15, IADR-0070: 段階ゲートの合格・撤退基準の入力＝段階別実績（StagePerformance）の永続化ポート。
// 未記録時は fail-safe 既定（BacktestPassed=false ほか全 false/0）を返し、既定で昇格を許可しない安全側に倒す。
// 実供給（バックテスト verdict・実DD・統制違反・スリッページ実績）は後続（BacktestService からの s2s・#82 系）で Save に流す。
public interface IStagePerformanceStore
{
    /// <summary>現在の段階別実績を返す。未記録なら既定値（BacktestPassed=false）＝昇格不可の安全側。</summary>
    StagePerformance GetCurrent();

    /// <summary>段階別実績を upsert する（単一行）。後続の実供給がこの受け口に流し込む。</summary>
    void Save(StagePerformance performance);
}
