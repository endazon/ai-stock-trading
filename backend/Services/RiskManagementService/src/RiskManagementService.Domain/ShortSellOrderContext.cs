namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016: 空売り 1 件を判定するために、注文意図・統制設定・ポートフォリオ状態の
// **いずれからも導けない**外部由来の入力。借株の可否（一次ゲート）と料率（ブローカー照会）・現在の維持率・
// 権利確定日（コーポレートアクション）・空売り建玉のエクスポージャ・強制買戻し由来の禁止期限。
//
// #329 第 2 段階, IADR-0131: 本文脈が **null**（供給経路が無い）ときは空売りを**通さない**。
// ADR-0016 決定3 は「発注前に借株料を照会できない場合、空売り自体を行わない」と定めており、
// 「照会できないなら素通しする」に倒すと、年率 100% の銘柄でも発注が通る穴が残る（フェイルクローズ）。
// 供給元（moomoo の借株照会・建玉射影・コーポレートアクション）の実装は #342 の PoC 結果に依存するため
// 本 issue の範囲外である。判定コアだけを先に確定する。
public record ShortSellOrderContext
{
    /// <summary>判定日（現地営業日）。権利確定日・強制買戻し禁止期限との突き合わせに用いる。</summary>
    public required DateOnly Today { get; init; }

    /// <summary>
    /// 借株料（**年率・比率**。0.20 ＝ 年率 20%）。**null は「事前照会できなかった」**を意味し、
    /// 拒否理由 <c>BorrowUnavailable</c> となる（ADR-0016 決定3。約定後に取得して手仕舞う案は、
    /// 約定した時点で既にリスクを取っており統制にならない）。
    /// <para>
    /// FR-10, ADR-0016 決定3（2026-08-06 改訂）, IADR-0158 決定3: **単位が確定した年率だけを与える。**
    /// moomoo `TrdGetMarginRatio.ShortFeeRate`（実測 `1.5`）は**単位が未確定**であり、そのまま写像しては
    /// ならない——`1.5` を年率（＝150%）と読めば上限 20% を 7.5 倍超過して**全銘柄が拒否**され、
    /// `1.5%`（＝0.015）と読めば**何も弾かない**。同じ値が単位の読み方ひとつで正反対に振れる。
    /// </para>
    /// </summary>
    public decimal? BorrowRateAnnual { get; init; }

    /// <summary>
    /// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, IADR-0158 決定1: **空売りの一次ゲート**——
    /// 当該銘柄の借株が許可されているか。供給元は moomoo `TrdGetMarginRatio.IsShortPermit`
    /// （実弾口座のヘッダでのみ取得できる。IADR-0144 決定3）である。
    /// <para>
    /// 既定 <c>false</c> ＝ 借りられない（安全側）。**<c>false</c> は「借株不可」と「照会できていない」の
    /// 両方を含み、いずれも拒否理由 <c>BorrowUnavailable</c> へ写像する**（都度の借株需給による locate 失敗＝
    /// Reg SHO 由来の制限そのものであるため。決定3 改訂が「新しいコードは追加しない」と明示した）。
    /// </para>
    /// </summary>
    public bool ShortPermit { get; init; }

    /// <summary>
    /// 強制買戻し（buy-in）由来の空売り禁止の解除日。null は禁止なし。ADR-0016 決定4（30 日間）。
    /// 解除日の算出は <see cref="ShortSellingLimits.BuyInBanUntil"/>。
    /// 期間中の拒否理由は <c>BuyInBanned</c> であり、<c>BorrowUnavailable</c> ではない（決定10）。
    /// </summary>
    public DateOnly? BuyInBanUntil { get; init; }

    /// <summary>
    /// 当該銘柄の権利確定日（判明していれば）。**前日**の新規空売りを禁止する（ADR-0016 決定5）。
    /// 配当利回りの高い銘柄では、1 日の期待利幅を配当相当額の支払いが上回る。
    /// </summary>
    public DateOnly? DividendRecordDate { get; init; }

    /// <summary>
    /// 維持率判定の入力（純資産と信用建玉の束）。**null は「供給経路が無い／取得できなかった」**を意味する。
    /// 空売り建玉を既に保有している場合は、維持率を確認できないまま積み増さない
    /// （<c>MaintenanceMarginBreach</c>。IADR-0131 決定4）。
    /// <para>
    /// FR-10, ADR-0016 決定7（2026-08-07 追記）, #420, IADR-0160: **維持率（<c>decimal?</c>）ではなく
    /// スナップショットを受ける。** 口座へ適用する閾値は**建玉ごとの閾値の最大値**であり、算出には保有建玉の
    /// 株価と商品種別が要る。従前は維持率だけを受け取っていたため、評価器は「これから出す注文の株価」しか
    /// 閾値の材料を持たず、**自動縮小（<see cref="MaintenanceMarginReducer"/>）より緩い閾値**で判定していた。
    /// </para>
    /// <para>
    /// **縮小側と同じ型を受ける**ことで、同じ入力に対して同じ適用閾値が出ることが構造的に保証される。
    /// 維持率も同じ束から導出されるため（<see cref="MaintenanceMarginSnapshot.MaintenanceMarginRatio"/>）、
    /// 算式（純資産 ÷ 建玉評価額の合計）が判定の 2 か所で食い違うことがない。
    /// **供給されない建玉を 0 件や株価 0 で埋めた偽の束を作らないこと**——「観測した結果ゼロだった」と
    /// 読める値を発明することになる（IADR-0154 / IADR-0159 と同じ規律）。供給が無いなら <c>null</c> にする。
    /// </para>
    /// </summary>
    public MaintenanceMarginSnapshot? MarginSnapshot { get; init; }

    /// <summary>当該銘柄の既存空売り建玉の評価額（基準通貨）。1 銘柄あたり上限（equity の 10%）の累計判定に用いる。</summary>
    public decimal SymbolShortExposure { get; init; }

    /// <summary>空売り建玉の合計（基準通貨）。空売り比率 50% の判定の分子。</summary>
    public decimal TotalShortExposure { get; init; }

    /// <summary>建玉総額（ロング＋ショート・基準通貨）。空売り比率 50% の判定の分母。</summary>
    public decimal TotalExposure { get; init; }
}
