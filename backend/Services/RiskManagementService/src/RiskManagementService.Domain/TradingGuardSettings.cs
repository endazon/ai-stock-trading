using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-19, ADR-0007: 取引ガードのソフト設定。変更は利用者のみが行える（生成AI・自動処理は変更不可）
public record TradingGuardSettings
{
    /// <summary>
    /// FR-19, ADR-0016 決定1, #332: 有効な商品種別（現物 / 信用買い / 空売り の 3 値を独立に制御する）。
    /// 既定は「現物のみ有効」。**空売りの有効・無効もここが単一情報源である**（IADR-0132 決定2）。
    /// </summary>
    public required IReadOnlySet<ProductType> EnabledProductTypes { get; init; }

    public required IReadOnlySet<Market> EnabledMarkets { get; init; }

    /// <summary>取引禁止銘柄（利用者が理由・登録日を伴って登録する。ADR-0007 §決定）。</summary>
    public required IReadOnlyCollection<BannedSymbol> BannedSymbols { get; init; }

    /// <summary>
    /// FR-19, #375, ADR-0021 決定3: <b>利用者が設定した</b>口座種別。既定は<b>信用口座</b>
    /// （ADR-0021 決定1「信用口座を既定とし、現金口座は拡張として対応する」）。
    /// <para>
    /// <b>本値は統制の切り替えには使わない。</b> 統制はブローカーへ照会した結果
    /// （<c>PortfolioSnapshot.Account</c>）で切り替える。本値の役割は<b>照会結果との食い違いを検知すること</b>
    /// だけである（決定3。同 ADR 106 行「照会と設定の二重化は冗長に見えるが、**食い違いの検知そのものが目的**」）。
    /// </para>
    /// <para>
    /// 既定を信用口座とすることは「照会できなければ信用口座として扱う」ことを意味しない。
    /// 照会結果が無ければ<b>新規建てを止める</b>（fail-closed）。
    /// </para>
    /// </summary>
    public AccountType ConfiguredAccountType { get; init; } = AccountType.Margin;

    /// <summary>
    /// 差金決済防止: 同一銘柄の同日再エントリー禁止。
    /// <para>
    /// **適用範囲は口座種別に依存する**（#375・ADR-0021 決定4-1）。信用口座では**日本株の現物取引のみ**
    /// （日本の差金決済規制〔金商法 161 条の 2〕向け。米国株は信用口座では Good Faith Violation が発生しない。
    /// #332・IADR-0132 決定5）。**現金口座では米国株にも適用する**（GFV が発生するため）。
    /// 判定の単一情報源は <see cref="AccountTypePolicy.AppliesSameDayReentry"/> である。
    /// </para>
    /// </summary>
    public bool PreventSameDayReentry { get; init; } = true;

    /// <summary>
    /// 相場操縦とみなされ得る発注パターン（約定意思のない発注・板演出・過剰な訂正/取消）の禁止（FR-19）。
    /// 既定で有効。実際の検知は <see cref="IManipulativeOrderPatternDetector"/> の注入実装が担う（後続スライス）。
    /// </summary>
    public bool ProhibitManipulativeOrderPatterns { get; init; } = true;
}
