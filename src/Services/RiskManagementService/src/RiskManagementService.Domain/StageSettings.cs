using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008: 運用段階（段階ゲート）。遷移は合格・撤退基準に基づき利用者の承認で行う。
// 数値は明示的に昇順で固定する。段階ゲートの遷移ロジック（StageGate: 昇格＝現段階の 1 段上、
// 方向判定＝数値比較）と設定の直列化（既定 JSON は enum を数値で往復）が、この連続昇順の割り当てに
// 依存するため、値の挿入・並べ替えを行わない（追加は末尾に連番で行う）。
public enum TradingStage
{
    Stage0Verification = 0,
    Stage1Paper = 1,
    Stage2MinimalLive = 2,
    Stage3ScaledLive = 3,
}

// FR-20: 段階ごとの動作モード（ペーパー/実弾）と資金上限を強制する
public record StageSettings(
    TradingStage Stage,
    TradeMode Mode,
    decimal CapitalCap);
