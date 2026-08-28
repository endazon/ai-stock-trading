using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008: 段階ごとの動作モード・資金上限（StageSettings）と撤退基準の倍率を定める方針。
// 既定は TradingDefaults.CreateStagePolicy()。実弾段階（Stage 2/3）の資金上限は保守的な暫定既定とし、
// 実運用値は利用者が FR-17 設定で確定・変更する（IADR-0041）。
public record StageGatePolicy
{
    /// <summary>段階ごとの設定（モード・資金上限）。4 段階すべてを含む。</summary>
    public required IReadOnlyDictionary<TradingStage, StageSettings> Definitions { get; init; }

    /// <summary>撤退基準の DD 倍率。実DD ≥ バックテスト最大DD × 本値 で撤退。既定 1.5（ADR-0008）。</summary>
    public decimal WithdrawalDrawdownMultiple { get; init; } = 1.5m;

    /// <summary>
    /// FR-20, #333, 06_daytrading-review §4.1〜§4.3: Stage 1（SIMULATE）の合格条件の閾値
    /// （60 営業日 / 100 件 / 累計 120 営業日で打ち切り）。**テストは本値から引き、閾値を直書きしない。**
    /// </summary>
    public Stage1GateCriteria Stage1Criteria { get; init; } = Stage1GateCriteria.Default;

    /// <summary>
    /// 指定段階の設定（モード・資金上限）を返す。<see cref="Definitions"/> は 4 段階すべてを含むことが前提
    /// （既定 <c>TradingDefaults.CreateStagePolicy()</c> は全段階を埋める）。該当段階が無い場合は
    /// <see cref="KeyNotFoundException"/> を投げる（4 段階を欠く方針は設定不備として早期に失敗させる）。
    /// </summary>
    public StageSettings SettingsFor(TradingStage stage) => Definitions[stage];
}
