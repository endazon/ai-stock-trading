namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016: 空売り 1 件を判定するために、注文意図・統制設定・ポートフォリオ状態の
// **いずれからも導けない**外部由来の入力。借株の可否と料率（ブローカー照会）・現在の維持率・
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
    /// 借株料（年率）。**null は「事前照会できなかった」**を意味し、拒否理由 <c>BorrowUnavailable</c> となる
    /// （ADR-0016 決定3。約定後に取得して手仕舞う案は、約定した時点で既にリスクを取っており統制にならない）。
    /// </summary>
    public decimal? BorrowRateAnnual { get; init; }

    /// <summary>借株の locate が成立したか（Reg SHO）。既定 false ＝ 借りられない（安全側）。</summary>
    public bool BorrowAvailable { get; init; }

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
    /// 現在の維持率。**null は「取得できなかった」**を意味する。空売り建玉を既に保有している場合は
    /// 維持率を確認できないまま積み増さない（<c>MaintenanceMarginBreach</c>。IADR-0131 決定4）。
    /// </summary>
    public decimal? MaintenanceMarginRatio { get; init; }

    /// <summary>当該銘柄の既存空売り建玉の評価額（基準通貨）。1 銘柄あたり上限（equity の 10%）の累計判定に用いる。</summary>
    public decimal SymbolShortExposure { get; init; }

    /// <summary>空売り建玉の合計（基準通貨）。空売り比率 50% の判定の分子。</summary>
    public decimal TotalShortExposure { get; init; }

    /// <summary>建玉総額（ロング＋ショート・基準通貨）。空売り比率 50% の判定の分母。</summary>
    public decimal TotalExposure { get; init; }
}
