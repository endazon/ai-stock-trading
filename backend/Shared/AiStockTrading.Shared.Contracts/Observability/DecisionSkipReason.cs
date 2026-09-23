namespace AiStockTrading.Shared.Contracts.Observability;

/// <summary>
/// FR-04, FR-10, NFR-07, #891, IADR-0374: <b>取引判断が発注意図を作らなかった（見送った）理由の語彙。</b>
/// <para>
/// 🔴 <b>1 件だけを特別扱いしない。</b> 本列挙は <c>TradeDecisionAppService</c> の見送り地点を
/// <b>一度に全件洗い出して</b>作った（#891 やること 1）。理由を 1 つだけ足すと、
/// 「その他」が何を含むのかが読み手ごとに割れ、集計が意味を失う。
/// </para>
/// <para>
/// 🔴 <b>これは観測の語彙であって、監査台帳のイベントではない。</b> 監査・通知へ載せるかどうかは
/// 本列挙の結論を見てから決める（IADR-0358 決定 4 が <c>TradeDecisionSkipped</c> /
/// <c>OrderDispatchForgone</c> の流用を「誤帰属になる」として棄却したのは今も有効である）。
/// </para>
/// <para>
/// <b>値を足すときは末尾へ足す</b>（メトリクスのタグ値は名前で出るため順序に意味は無いが、
/// 既存の値の意味を変えないための規律である）。
/// </para>
/// </summary>
public enum DecisionSkipReason
{
    /// <summary>FR-07: 確定済み日報の方針が無い（確定前方針は不適用）。</summary>
    DailyPolicyUnconfirmed,

    /// <summary>FR-02, IADR-0099 決定3: 現在値ソースが有効なのに現在値が取得不能・鮮度切れ。</summary>
    CurrentPriceUnavailable,

    /// <summary>FR-10, FR-17, IADR-0107: 基準通貨への換算レートがまったく解決できない（値が無い）。</summary>
    FxRateUnresolved,

    /// <summary>
    /// FR-10, ADR-0022 決定5, IADR-0197: 換算レートが鮮度切れで、かつ保有が無い／不明
    /// （LLM を呼ぶ前に倒す。手仕舞いの余地が無いため）。
    /// </summary>
    FxRateStaleNoHolding,

    /// <summary>FR-04, ADR-0003: LLM が Hold を返した（<b>平常時に最も多い</b>見送りである）。</summary>
    LlmHold,

    /// <summary>
    /// 🔴 FR-04, FR-10, #865, IADR-0358: <b>保有状況の照会先が実結線なのに保有が不明で、新規建てを見送った。</b>
    /// <para>
    /// <b>平常時の期待値は 0 件である</b> —— 未結線（既定の NoOp）ではこの見送りは発生せず、
    /// 立つのは照会が失敗したときだけである。したがって「続いている」こと自体が異常であり、
    /// アラートの 1 件目がこの値を見る（IADR-0374 決定 5）。
    /// </para>
    /// </summary>
    HoldingsUnknownOpen,

    /// <summary>FR-10, IADR-0119 決定2: 保有なし・不明での売り＝裸の新規ショート建てを見送った。</summary>
    NakedShortOpen,

    /// <summary>FR-02, IADR-0099: 参照価格が不正（0 以下）。</summary>
    ReferencePriceInvalid,

    /// <summary>
    /// FR-10, ADR-0022 決定5, IADR-0197: 換算レートが鮮度切れで、建玉効果が新規建て（手仕舞いは止めない）。
    /// </summary>
    FxRateStaleOpen,

    /// <summary>FR-03, IADR-0035: 損切り幅が不正（0 以下・参照価格以上）。</summary>
    StopLossDistanceInvalid,

    /// <summary>FR-04, FR-10: サイジングの結果が数量 0（統制上限・残枠・リスク基準サイズのいずれかで潰れた）。</summary>
    SizingZeroQuantity,

    /// <summary>FR-17, IADR-0076: 採算ゲートが不成立、または費用見積り不能（安全側で見送り）。</summary>
    ProfitabilityNotViable,
}
