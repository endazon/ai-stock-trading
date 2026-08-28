using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-19, ADR-0007, IADR-0006: 相場操縦とみなされ得る発注パターンの判定ポート（拡張ポイント）。
// 実装は注文履歴の統計（過剰な訂正/取消・約定意思のない発注・板演出）を入力に判定する後続スライスの責務。
// 本スライスでは拡張点のみを定義し、RiskEvaluator はこの検出器が注入されたときにのみ判定を呼ぶ。
public interface IManipulativeOrderPatternDetector
{
    /// <summary>
    /// 当該注文が相場操縦とみなされ得る発注パターンに該当するかを判定する。
    /// true を返すと <see cref="RejectionReason.ManipulativeOrderPattern"/> で拒否される。
    /// </summary>
    bool IsSuspectedManipulation(OrderIntent intent, PortfolioSnapshot snapshot);
}
