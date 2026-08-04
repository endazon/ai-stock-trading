namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016（決定2,3,4,7,9）: 空売り（信用売り）専用の統制値。
// 空売りは**損失に上限が無い**（株価は理論上無限に上昇し得る）ため、損切りが機能する前提で作られた
// 既存の統制（1 取引あたりリスク 1% 等）だけでは資金保全が成立しない。したがって独立した統制群を持つ。
//
// #329 第 2 段階, IADR-0131: 金額で表す統制は equity 比で保持する（IADR-0130 決定1 と同じ方針。
// 比率→金額の解決は本レコードの解決メソッドだけを通す）。値の出典は 05_trading-assumptions §5 と
// ADR-0016 であり、実装が発明した値は 1 つも無い。
public record ShortSellingLimits
{
    /// <summary>
    /// 1 銘柄あたりの空売り建玉の上限（equity 比。既定 0.10 ＝ equity の 10%。自己資金 $3,000 で $300）。
    /// ADR-0016 決定2(a)・§5。逆指値（決定2(b)）と**両方**課す。逆指値だけでは寄付きのギャップに効かず、
    /// 金額の絶対上限が無ければ 1 銘柄の急騰で口座を失う。解決は <see cref="PerSymbolCapFor"/>。
    /// </summary>
    public required decimal PerSymbolCapRatio { get; init; }

    /// <summary>
    /// 借株料の上限（年率。既定 0.20 ＝ 年率 20%）。ADR-0016 決定3・§5。
    /// 年率 20% ≒ 1 日 0.055%。借りにくい銘柄ほど借株料が高く踏み上げリスクも高いため、
    /// **コストと危険度を同じ閾値で弾ける**。超過は拒否し、**照会できない場合は空売り自体を行わない**。
    /// </summary>
    public required decimal BorrowRateCapAnnual { get; init; }

    /// <summary>
    /// 自前の維持率閾値（既定 0.40 ＝ 40%）。ADR-0016 決定7・§5。
    /// 実際に用いる閾値は**本値と規制上の要求のうち厳しい方**であり、解決は
    /// <see cref="MaintenanceMarginThresholdFor"/>（株価に依存する）。
    /// </summary>
    public required decimal MaintenanceMarginThreshold { get; init; }

    /// <summary>
    /// 維持率割れによる自動縮小の回復目標オフセット（既定 0.05 ＝ +5 ポイント）。
    /// §5（利用者裁定 2026-08-02・project-planning#90 第 10 回）・FR-10・UC-06。
    /// **回復目標は閾値に連動する**（閾値 40% なら目標 45%、規制側が効いて閾値 45% なら目標 50%）。
    /// ちょうど閾値まで戻すと次の小さな価格変動で再び発動するため、5 ポイントの余裕で**発動の連鎖を防ぐ**。
    /// 解決は <see cref="MaintenanceRecoveryTargetFor"/>。
    /// <para>縮小の実行（必要証拠金の降順・最小限の決済）は本 issue の範囲外である（#330 の担当）。</para>
    /// </summary>
    public required decimal MaintenanceRecoveryTargetOffset { get; init; }

    /// <summary>
    /// 空売り対象の株価下限（**USD 建て**。既定 5.00）。ADR-0016 決定7・§5。
    /// $5.00 未満は規制上の維持証拠金が時価の 100% 超となるため対象外とする。
    /// **この除外を禁止銘柄（<c>RejectionReason.BannedSymbol</c>）で表現してはならない**
    /// （市況由来の事象をクラス C ＝「AI が禁止事項を犯そうとした件数」に混入させないこと。ADR-0016 決定10）。
    /// </summary>
    public required decimal PriceFloorUsd { get; init; }

    /// <summary>
    /// 空売り比率の上限（既定 0.50 ＝ 建玉総額の 50%）。ADR-0016 決定9・§5。
    /// 保有建玉数の上限は 3 であり、3 銘柄すべてを空売りにすると市場全体の上昇で全建玉が同時に損失を出す。
    /// </summary>
    public required decimal ExposureRatioCap { get; init; }

    /// <summary>
    /// 強制買戻し（recall / buy-in）を検知した銘柄を空売り禁止にする日数（既定 30 日）。ADR-0016 決定4。
    /// 強制買戻しは防げないが、発生した銘柄は**借株需給が逼迫しているという強いシグナル**である。
    /// 期限の算出は <see cref="BuyInBanUntil"/>。
    /// </summary>
    public required int BuyInBanDurationDays { get; init; }

    /// <summary>
    /// 規制上の実効維持率の下限（30%。FINRA Rule 4210(c)）。ADR-0016 決定7 が
    /// <c>max($5.00 ÷ 株価, 30%)</c> と明記した式の右辺である。設定ではなく**規制の定数**のため
    /// 利用者が変更できる設定値（上のプロパティ群）とは分けて保持する。
    /// </summary>
    public const decimal RegulatoryMaintenanceMarginFloor = 0.30m;

    /// <summary>
    /// 規制上の 1 株あたり固定維持証拠金（$5.00。FINRA Rule 4210(c)）。上式の左辺の分子である。
    /// <see cref="PriceFloorUsd"/> と数値は等しいが**意味が異なる**（こちらは維持証拠金の固定額、
    /// あちらは空売り対象の株価下限）。同じ値だからと 1 つにまとめると、片方が改定されたときに
    /// もう片方が黙って追随してしまう。
    /// </summary>
    public const decimal RegulatoryFixedMaintenancePerShareUsd = 5.00m;

    /// <summary>
    /// FR-10, ADR-0016 決定2(a): 1 銘柄あたりの空売り上限を equity から解決する。
    /// </summary>
    /// <param name="equity">
    /// 判定に用いる自己資金（equity）＝前営業日終値時点の評価額（IADR-0130 決定2。
    /// 実装上は <c>PortfolioSnapshot.Capital</c>）。
    /// </param>
    public decimal PerSymbolCapFor(decimal equity) => equity * PerSymbolCapRatio;

    /// <summary>
    /// ADR-0016 決定7: 規制側の実効維持率 <c>max($5.00 ÷ 株価, 30%)</c>。
    /// </summary>
    /// <param name="priceUsd">株価（USD）。空売り対象は $5.00 以上に限られる（<see cref="PriceFloorUsd"/>）。</param>
    public static decimal RegulatoryMaintenanceMarginFor(decimal priceUsd)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priceUsd);
        return Math.Max(RegulatoryFixedMaintenancePerShareUsd / priceUsd, RegulatoryMaintenanceMarginFloor);
    }

    /// <summary>
    /// FR-10, ADR-0016 決定7: 実際に用いる維持率閾値 ＝「自前の 40%」と「規制上の要求」の**厳しい方**。
    /// <para>
    /// 両者が一致するのは株価 **$12.50**（<c>5 ÷ 0.4</c>）である。$12.50 未満では規制要求が 40% を上回るため
    /// 規制側が効き、$12.50 以上では自前の 40% が効く。**$16.67 と混同しないこと**——$16.67 は規制側の内訳が
    /// 固定額 $5.00 から時価の 30% へ入れ替わる点であり、40% との大小が入れ替わる点ではない
    /// （計画 §5 の 2026-08-01 追補2 で是正された）。
    /// </para>
    /// </summary>
    public decimal MaintenanceMarginThresholdFor(decimal priceUsd) =>
        Math.Max(MaintenanceMarginThreshold, RegulatoryMaintenanceMarginFor(priceUsd));

    /// <summary>
    /// FR-10, UC-06, §5: 維持率割れによる自動縮小の回復目標 ＝ 適用される閾値 ＋ 5 ポイント。
    /// 閾値が規制側で上がれば回復目標も同じだけ上がる（連動する）。
    /// </summary>
    public decimal MaintenanceRecoveryTargetFor(decimal priceUsd) =>
        MaintenanceMarginThresholdFor(priceUsd) + MaintenanceRecoveryTargetOffset;

    /// <summary>
    /// ADR-0016 決定4: 強制買戻しを検知した日から起算した空売り禁止の解除日（当日を含まない）。
    /// </summary>
    public DateOnly BuyInBanUntil(DateOnly detectedOn) => detectedOn.AddDays(BuyInBanDurationDays);
}
