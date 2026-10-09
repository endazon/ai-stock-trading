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

    /// <summary>
    /// 🔴 FR-04, FR-10, #934, IADR-0390 決定5: <b>保有状況の照会先が実結線なのに当日の未約定の新規建て注文が不明で、
    /// 新規建てを見送った。</b> <see cref="HoldingsUnknownOpen"/>（約定済みの保有が不明）と同じ形の別地点である。
    /// <para>
    /// <b>平常時の期待値は 0 件である</b> —— 立つのは実結線で未約定の照会（<c>GET /risk-controls/working-entry-orders</c>）が
    /// 失敗したときだけである。したがって <c>AstEntriesBlockedByUnknownHoldings</c> が本値も見る。
    /// </para>
    /// </summary>
    WorkingEntriesUnknownOpen,

    /// <summary>
    /// FR-10, FR-04, #1113, IADR-0463 決定 4: <b>LLM を呼ぶ前</b>の見送り。保有が既知で 0・未約定の新規建てが既知で空の銘柄で、
    /// リスク管理の新規建ての可否の口（審査と同じ述語）が買いの新規建ては必ず拒否されると答えた。
    /// <para>
    /// 🔴 計器の移動: 是正前は同じ新規建てが LLM を呼んだ後に審査で拒否され、<c>ast.risk.rejections{reason}</c> に出ていた。
    /// その一部がこの値へ移る（審査は変わらない）。<c>TradeDecisionForgoneBeforeLlm</c> の同名の値と一致させる。
    /// </para>
    /// </summary>
    EntryBlockedByRiskControls,

    /// <summary>
    /// FR-10, FR-04, #1130, IADR-0471 決定 3: <b>LLM を呼んだ後</b>の見送り。保有中の銘柄で、LLM の前に読んだリスク管理の新規建ての可否の口
    /// （審査と同じ述語）がその方向の新規建て（買い増し・売り増し）は必ず拒否されると答えていたのに、LLM が買い増し・売り増しを返した。
    /// 発注意図を作らず Hold に倒す（決済は対象外）。<c>TradeDecisionHeld</c> の理由にもこの名前が載る。
    /// <para>
    /// 🔴 計器の移動: 是正前は同じ買い増しが審査で拒否され、<c>ast.risk.rejections{reason}</c> に出ていた。その一部がこの値へ移る（審査は変わらない）。
    /// </para>
    /// </summary>
    AddOnBlockedByRiskControls,

    /// <summary>
    /// FR-10, #1176, IADR-0495 決定2: <b>LLM を呼ぶ前</b>の見送り。保有が既知で 0・未約定の新規建てが既知で空の銘柄で、新規建てに使える
    /// 金額の上限（1 注文上限・段階残枠・日次残枠の最小）が最小の名目額（equity × しきい値。既定 1%）に届かない。
    /// <c>TradeDecisionForgoneBeforeLlm</c> の同名の値と一致させる。
    /// </summary>
    EntryCapacityBelowMinimumNotional,

    /// <summary>
    /// FR-10, #1176, IADR-0495 決定1: <b>LLM を呼んだ後</b>の見送り。新規建て（買い増し・売り増しを含む）のサイジングの結果
    /// （数量 × 参照価格・基準通貨）が最小の名目額（equity × しきい値。既定 1%）に満たない。ちょうど等しいときは見送らない。
    /// <c>TradeDecisionHeld</c> の理由にもこの名前が載る。決済は対象外。
    /// </summary>
    SizedBelowMinimumNotional,

    /// <summary>
    /// FR-10, #1174, IADR-0500 決定1・2: <b>LLM を呼ぶ前</b>の見送り。保有が既知で 0・未約定の新規建てが既知で空の銘柄で、段階残枠と日次残枠
    /// （いずれも既知）の小さい方が現在値（基準通貨へ換算）× 1 株に満たない（サイジングは必ず数量 0）。
    /// <para>
    /// 🔴 計器の移動: 是正前は同じ判断が LLM を呼んだ後に <see cref="SizingZeroQuantity"/> で数えられていた（GOOGL で 1 セッション 48 回）。その一部がこの値へ移る。
    /// 残枠が最小の名目額にも届かないときは <see cref="EntryCapacityBelowMinimumNotional"/> が先に当たる。<c>TradeDecisionForgoneBeforeLlm</c> の同名の値と一致させる。
    /// </para>
    /// </summary>
    EntryCapacityBelowOneShare,

    /// <summary>
    /// FR-02, FR-04, #1286, IADR-0521 決定 2: <b>LLM を呼んだ後</b>の見送り。定時サイクルで監視銘柄の外の保有銘柄（保有のみ）を出口専用で
    /// 判断したのに、LLM の結論が新規建て（買い増し・売り増し、または保有が 0 になった後の新規建て）だった。発注意図を作らず Hold に倒す
    /// （決済は対象外）。<c>TradeDecisionHeld</c> の理由にもこの名前が載る。監視銘柄の外への新規建ての可否は計画に定めが無いため、出口に限る。
    /// </summary>
    ExitOnlyOpenOutsideWatchlist,

    /// <summary>
    /// FR-02, FR-04, #1286, IADR-0521 決定 2: <b>LLM を呼ぶ前</b>の見送り。監視銘柄の外の保有銘柄（保有のみ）を出口専用で判断しようとしたが、
    /// 判断の前に引いた保有が 0 または不明だった（決済は保有が分かっていなければ成立せず、新規建ては出口専用で出さないため、LLM の結論に依らず
    /// 発注意図は作られない）。<c>TradeDecisionForgoneBeforeLlm</c> の同名の値と一致させる。
    /// </summary>
    ExitOnlyWithoutHolding,
}
