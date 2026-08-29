using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// FR-19, FR-10, #375, ADR-0021 決定2〜5, IADR-0153: 口座種別に依存する統制の**単一情報源**（純関数）。
/// <para>
/// ADR-0021 が「口座種別に依存する統制の切り替えが 1 か所で定義され、各文書はここを参照できる」ことを
/// 良い影響として挙げている（同 ADR 112 行）。実装側でも 1 か所に集める。
/// </para>
/// </summary>
public static class AccountTypePolicy
{
    /// <summary>
    /// FR-19, ADR-0021 決定4-3: Good Faith Violation の**警告・停止のしきい値**（件数）。
    /// <para>
    /// 計画は「**2 回目で警告、3 回目の手前で新規建てを停止**」と定める。**警告と停止は同じ回数で立つ**——
    /// 3 回目が起きた時点で 90 日の口座制限という**不可逆**な結果が確定するため、
    /// 「2 回に達したら警告し、かつ 3 回目を出させない」ことが決定4-3 の要求である。
    /// </para>
    /// </summary>
    public const int GoodFaithViolationStopThreshold = 2;

    /// <summary>
    /// FR-19, ADR-0021 決定4-4/決定5: その口座種別で**成立し得る**商品種別か。
    /// <para>
    /// 現金口座では信用買い・空売りができない（株を借りられない）。これは
    /// 「空売りが無効に設定されている」（<c>ShortSellDisabled</c>・ADR-0016 決定1）とは**別の状態**であり、
    /// 設定ではなく**口座の能力**の問題である（ADR-0021 決定5）。
    /// </para>
    /// </summary>
    public static bool Supports(AccountType accountType, ProductType productType) => accountType switch
    {
        AccountType.Cash => productType == ProductType.Cash,
        // 信用口座は 3 種すべてを扱える（実際に発注できるかは利用者設定と段階ゲートが別途決める）。
        _ => true,
    };

    /// <summary>
    /// FR-19, ADR-0021 決定5: その口座種別で **ADR-0016（空売り）の統制群を評価するか**。
    /// <para>
    /// 現金口座では空売り自体が成立しないため、同 ADR の**全決定が適用対象外**である。
    /// 借株料・維持率・権利確定日といった空売り専用の拒否理由を返しても、実態（口座の能力の問題）と
    /// 食い違うだけである。
    /// </para>
    /// <para>
    /// **口座種別が不明（null）なら評価する。** 空売り統制はそれ自体がフェイルクローズであり
    /// （<c>ShortSellOrderContext</c> が null なら通さない）、評価を省く方が緩む側になる。
    /// </para>
    /// </summary>
    public static bool AppliesShortSellControls(AccountType? accountType) => accountType != AccountType.Cash;

    /// <summary>
    /// FR-19, ADR-0021 決定4-1, IADR-0132 決定5: 差金決済防止（同一銘柄の同日再エントリー禁止）を
    /// **その市場・その商品種別・その口座種別に適用するか**。
    /// <para>
    /// 適用条件は 2 系統の重ね合わせである。
    /// <list type="bullet">
    /// <item><b>日本の差金決済規制</b>（金商法 161 条の 2・06_daytrading-review §2.1）: 日本株の**現物**。
    /// 口座種別に依存しない（#332 / IADR-0132 決定5 の限定はこちらであり、**巻き戻さない**）。</item>
    /// <item><b>Good Faith Violation</b>（ADR-0021 決定4-1）: **現金口座**では米国株にも同じ禁止を適用する。
    /// 信用口座では売却代金を決済前に再利用できるため発生しない。</item>
    /// </list>
    /// </para>
    /// <para>
    /// いずれも**現物**に限る。信用（信用買い・空売り）は同一保証金での同日無制限回転が可能である
    /// （05_trading-assumptions §5「差金決済防止」）。
    /// </para>
    /// </summary>
    public static bool AppliesSameDayReentry(Market market, ProductType effectiveProductType, AccountType? accountType)
    {
        if (effectiveProductType != ProductType.Cash)
        {
            return false;
        }

        return market == Market.Japan || accountType == AccountType.Cash;
    }

    /// <summary>
    /// FR-19, ADR-0021 決定3, IADR-0153 決定2: その発注先に対して**口座種別の確認を要求するか**。
    /// <para>
    /// 内蔵 <c>paper</c>（<see cref="BrokerProvider.InternalPaper"/>）は**外部へ一度も発注しない擬似約定**であり、
    /// ブローカー口座そのものが存在しない。ADR-0021 はブローカー口座の存在を前提としており、
    /// この形を想定していない。**口座が無い以上、口座種別に依存する事故（GFV・差金決済）も起こり得ない。**
    /// </para>
    /// <para>
    /// 迂回路にはならない——内蔵 paper は外部へ注文を出さないため、確認を省いても侵し得る規制が無い。
    /// </para>
    /// </summary>
    public static bool RequiresVerifiedAccount(BrokerProvider provider) => provider != BrokerProvider.InternalPaper;

    /// <summary>
    /// FR-19, ADR-0021 決定4-3, ADR-0025 決定2, IADR-0165: GFV の発生回数が**新規建ての停止基準に達しているか**。
    /// <para>
    /// <b><c>null</c>（未供給）でも停止する。</b> 2 回に達していないことを確認できないためであり、
    /// 「分からないから通す」は 3 回で 90 日という不可逆な結果に対する統制にならない
    /// （ADR-0025 決定2 が「計数値が供給されない状態では新規建てを拒否する。fail-closed を維持する」と明記）。
    /// </para>
    /// <para>
    /// 引数は <see cref="GoodFaithViolationTally"/>（第一級の値）である。<c>int?</c> で受けると
    /// <c>count &gt;= 2</c> という**持ち上げ比較が未供給を黙って「違反なし」へ倒す**（IADR-0148 決定1 の罠）。
    /// </para>
    /// </summary>
    public static bool BlocksForGoodFaithViolations(GoodFaithViolationTally? tally) =>
        tally is null || tally.BlocksNewEntry;

    /// <summary>
    /// FR-19, ADR-0021 決定4-3: GFV の発生回数が**警告基準に達しているか**（2 回目で警告）。
    /// <para>
    /// 停止（<see cref="BlocksForGoodFaithViolations"/>）と同じ回数で立つ。**未供給は警告しない**——
    /// 警告は利用者へ「実際に 2 回起きた」ことを伝えるものであり、供給が無いことは
    /// <c>BrokerAccountTypeUnverified</c> / 停止の側で表す。
    /// </para>
    /// </summary>
    public static bool WarnsForGoodFaithViolations(GoodFaithViolationTally? tally) =>
        tally is not null && tally.Warns;

    /// <summary>
    /// FR-19, ADR-0021 決定4-2, ADR-0025, IADR-0153 決定4, IADR-0165 決定1:
    /// その買付が**決済済み資金で説明できないか**（＝未決済資金による買付か）。**判定の単一情報源である。**
    /// <para>
    /// 本述語は 2 か所から呼ばれ、**同じ事象を指すことが設計の要点**である（ADR-0025 決定2:
    /// 「計数の対象は ADR-0021 決定4 の統制2 が拒否しようとする事象と同じである」）。
    /// <list type="number">
    /// <item>発注前のガード（<c>RiskEvaluator</c>）——真なら <c>CashAccountSettlementHold</c> で拒否する</item>
    /// <item>事後の計数（<see cref="GoodFaithViolationDetection"/>）——ガードをすり抜けて約定した買付を 1 件記録する</item>
    /// </list>
    /// 2 か所に式を書くと、片方だけ直したときに「拒否はするが数えない」「数えるが拒否しない」がすり抜ける。
    /// </para>
    /// <para>
    /// <b>決済済み資金が未供給（<c>null</c>）なら真である</b>——「残高が分からないから通す」は統制にならない。
    /// </para>
    /// <para>
    /// <b>入力は <c>BrokerAccountState.SettledCashInBase</c> だけである。</b>
    /// <c>MaxTrdQtys.MaxCashBuy</c>（現金買付余力）・<c>Funds.AvlWithdrawalCash</c> / <c>MaxWithdrawal</c>
    /// （出金可能額）を代替に用いてはならない（ADR-0025）。とくに現金買付余力は現金口座では
    /// **未決済の売却代金を含む**のが通例であり、**それこそが GFV を引き起こす当の資金である。
    /// これを分母に据えると GFV 回避ガードが GFV を許可する。** 再混入は
    /// <c>scripts/check-banned-settled-cash-sources.js</c> が機械的に止める。
    /// </para>
    /// </summary>
    public static bool ExceedsSettledCash(decimal? settledCashInBase, decimal purchaseAmountInBase) =>
        settledCashInBase is not { } settled || purchaseAmountInBase > settled;
}
