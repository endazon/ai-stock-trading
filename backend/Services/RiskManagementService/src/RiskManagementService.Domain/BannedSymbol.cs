using AiStockTrading.Shared.Contracts.Trading;

// FR-19, ADR-0007: 取引禁止銘柄（利用者が登録。理由と登録日を記録する）。
// 計画（05_trading-assumptions §5「取引禁止銘柄リスト」）はグローリー 6457・デンソー 6902・
// 東芝 旧6502（再上場時に適用）を登録済みとし、システムの責務を
// 「登録されたものを確実に強制する」ことに限定する（ADR-0007 §理由）。
namespace RiskManagementService.Domain;

public record BannedSymbol(
    string Symbol,
    Market Market,
    string Reason,
    DateOnly RegisteredOn)
{
    /// <summary>
    /// FR-19, #332, IADR-0132 決定6: 注文の（銘柄コード, 市場）が本禁止登録に該当するか。
    /// <para>
    /// 市場は**厳密一致**（別市場の同一コードを誤拒否しないため。Issue #26）、銘柄コードは
    /// **前後空白を無視した大文字小文字を区別しない一致**とする。禁止銘柄は利用者が手で登録する
    /// 自由入力であり、単純な序数比較では `aapl` / `AAPL ` のような**表記差だけで禁止を迂回できる**。
    /// 禁止銘柄の拒否はクラス C（「AI が禁止事項を犯そうとした件数」）であり、
    /// 表記差による取りこぼしは段階昇格ゲートの証跡そのものを損なう。
    /// </para>
    /// </summary>
    public bool Matches(string symbol, Market market) =>
        Market == market
        && string.Equals(Symbol.Trim(), symbol?.Trim(), StringComparison.OrdinalIgnoreCase);
}
