using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0016: 空売り（新規売り建て）専用の統制 8 規則を発注前に判定する決定的コア。
// 違反は最初の 1 件で打ち切らず**全件列挙**する（FR-11 監査のため。RiskEvaluator と同じ規律）。
//
// 8 規則（ADR-0016 決定2,3,4,5,7,9・02_requirements FR-10 (1)〜(8)）:
//   (1) 1 銘柄あたり equity の 10% 上限            → ShortExposureExceeded
//   (2) 逆指値（ストップ注文）の同時発注必須        → StopOrderRequired
//   (3) **借株可否（一次ゲート）**／借株料 年率 20% 上限（残置・発火しない既知の統制）／
//       照会不可なら空売りしない                    → BorrowUnavailable / BorrowCostExceeded
//   (4) 維持率は 40% と規制要求の厳しい方          → MaintenanceMarginBreach
//   (5) 株価 $5.00 未満は対象外                    → ShortPriceFloorBreach
//   (6) 空売り比率 50% 上限                        → ShortExposureExceeded
//   (7) 権利確定日前日の新規空売り禁止              → DividendRecordDateNear
//   (8) 強制買戻し検知 → 30 日禁止                  → BuyInBanned（**BorrowUnavailable へ写像しない**）
public static class ShortSellEvaluator
{
    /// <summary>
    /// FR-10, IADR-0004/IADR-0119: 新規売り建て（＝空売り）か。売買方向（Side）だけでも
    /// 建玉効果（PositionEffect）だけでも判定できない——**両方の組**が空売りを一意に定める。
    /// </summary>
    public static bool IsShortEntry(OrderIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent is { Side: TradeSide.Sell, PositionEffect: PositionEffect.Open };
    }

    /// <summary>
    /// 空売り注文に固有の違反を全件列挙する。空売り以外の注文には適用しない（呼び出し側で
    /// <see cref="IsShortEntry"/> により振り分ける）。
    /// </summary>
    /// <param name="intent">注文意図。株価下限は <c>Price</c>（ローカル通貨＝米国株なので USD）で判定する。</param>
    /// <param name="shortSellEnabled">
    /// 空売りが有効か。**単一情報源は取引ガードの商品種別**（<c>Guard.EnabledProductTypes</c> が
    /// <see cref="ProductType.ShortSell"/> を含むか。#332・IADR-0132 決定2）。既定は無効である。
    /// </param>
    /// <param name="limits">空売り専用の統制値（ADR-0016 決定2,3,4,7,9）。</param>
    /// <param name="equity">自己資金（前営業日終値時点。IADR-0130 決定2）。1 銘柄あたり上限の基準。</param>
    /// <param name="context">外部由来の入力。**null は照会経路が無いことを意味し、空売りは通さない**。</param>
    public static IReadOnlyList<RejectionReason> Evaluate(
        OrderIntent intent,
        bool shortSellEnabled,
        ShortSellingLimits limits,
        decimal equity,
        ShortSellOrderContext? context)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(limits);

        var reasons = new List<RejectionReason>();

        // ADR-0016 決定1/8: 空売りが無効（既定）なら他を評価するまでもなく拒否する。
        if (!shortSellEnabled)
        {
            reasons.Add(RejectionReason.ShortSellDisabled);
        }

        // ADR-0016 決定13: 空売りの対象市場は**米国株のみ**（moomoo は日本株の信用取引を提供していない）。
        // 対象外市場でも通すと、USD 建ての株価下限 $5.00 を円建て株価と比較することになり
        // （¥300 > 5 で素通り）、統制がまるごと無効化される。市場ごと空売りを無効として扱う。
        if (intent.Market != Market.UnitedStates)
        {
            reasons.Add(RejectionReason.ShortSellDisabled);
        }

        // (5) 株価下限 $5.00（未満は対象外）。ADR-0016 決定7。
        if (intent.Price < limits.PriceFloorUsd)
        {
            reasons.Add(RejectionReason.ShortPriceFloorBreach);
        }

        // (2) 逆指値（ストップ注文）の同時発注必須。ADR-0016 決定2(b)・FR-10。
        // 逆指値が未約定・未受理であれば建玉を持たない。損切り価格を伴わない空売りは、
        // 損失に上限が無い取引を**損切り機構なしで**持つことに等しい。
        if (intent.StopLossPrice is null)
        {
            reasons.Add(RejectionReason.StopOrderRequired);
        }

        if (context is null)
        {
            // ADR-0016 決定3: 借株の可否・料率を事前照会できない場合は空売り自体を行わない（フェイルクローズ）。
            reasons.Add(RejectionReason.BorrowUnavailable);
            return reasons;
        }

        // (3) 借株の可否と料率。**一次ゲートは可否（IsShortPermit）であり、料率の閾値ではない。**
        // ADR-0016 決定3（2026-08-06 改訂）・IADR-0158・#417。
        //
        // 決定3 が当初前提とした「借りにくい銘柄ほど借株料が高い」は実測で成り立たなかった——借株在庫
        // （ShortPoolRemain）が 20 倍以上開いても ShortFeeRate は一律 1.5 である。**一律料率であれば
        // 20% の閾値は永久に超えず、その統制は何も弾かない。** 実際に危険な銘柄を弾いているのは
        // IsShortPermit（AMC・SPCE は False / 在庫 0 / 初期証拠金率 100）である。
        if (!context.ShortPermit)
        {
            // **一次ゲート**: 借株が許可されていない（または照会できていない）銘柄は空売りしない。
            // 拒否理由は既存の BorrowUnavailable（クラス A）へ写像し、**新しいコードを追加しない**
            // ——同コードは「都度の借株需給による locate 失敗」を表し、IsShortPermit=False
            // （借株在庫 0・Reg SHO 由来の制限）はまさにその事象である（決定3 改訂が明示）。
            reasons.Add(RejectionReason.BorrowUnavailable);
        }
        else if (context.BorrowRateAnnual is null)
        {
            // 決定3 の**未改訂部分**: 発注前に借株料を照会できない場合、空売り自体を行わない
            //（フェイルクローズ。改訂は一次ゲートを移しただけで、この縮退は残る）。
            reasons.Add(RejectionReason.BorrowUnavailable);
        }
        else if (context.BorrowRateAnnual > limits.BorrowRateCapAnnual)
        {
            // **二次**: 借株料 年率 20% 上限（決定3・決定10 の BorrowCostExceeded）。
            // **実測の情報源（moomoo の一律料率）では発火しない見込みの、既知の統制である。**
            // それでも落とさない——料率が銘柄別になった日に無防備になるためであり、
            // 「発火しない」ことと「無い」ことは別である（残置は決定3 改訂が明示的に指示している）。
            reasons.Add(RejectionReason.BorrowCostExceeded);
        }

        // (8) 強制買戻し（buy-in）検知銘柄は 30 日間空売りしない。ADR-0016 決定4/決定10。
        // 専用の理由 BuyInBanned で記録する。**BorrowUnavailable へ写像しない**（決定10 の 2026-08-04 追記）——
        // BorrowUnavailable は都度の借株需給による locate 失敗、BuyInBanned は期間の経過で解除される
        // 禁止状態であり、原因も解除条件も異なる。写像すると監査ログ（FR-11）の理由が実態と食い違う。
        // **禁止銘柄（BannedSymbol・クラス C）にも混ぜない**（市況由来の事象を「AI が禁止事項を
        // 犯そうとした件数」に混入させない）。30 日リストは BannedSymbol とは別の空売り専用リストである。
        // #419, IADR-0159 決定5: 判定式は BuyInBanPolicy を単一情報源とする（文脈が組めないときの供給経路
        // BuyInBanSupply と規則を共有する。同じ規則を 2 か所に書かない）。
        if (BuyInBanPolicy.IsBanned(context.Today, context.BuyInBanUntil))
        {
            reasons.Add(RejectionReason.BuyInBanned);
        }

        // (7) 権利確定日の**前日**は新規空売りを禁止する。ADR-0016 決定5。
        if (context.DividendRecordDate is { } recordDate && context.Today == recordDate.AddDays(-1))
        {
            reasons.Add(RejectionReason.DividendRecordDateNear);
        }

        // (1) 1 銘柄あたり equity の 10% 上限・(6) 空売り比率 50% 上限。いずれも ShortExposureExceeded。
        // 判定は「既存の空売り建玉 ＋ 当該注文」で行う（1 件ずつ上限内でも累計で超過できるため）。
        var notional = intent.NotionalInBase;
        var perSymbolExceeded = context.SymbolShortExposure + notional > limits.PerSymbolCapFor(equity);
        var ratioExceeded = context.TotalShortExposure + notional
            > (context.TotalExposure + notional) * limits.ExposureRatioCap;
        if (perSymbolExceeded || ratioExceeded)
        {
            reasons.Add(RejectionReason.ShortExposureExceeded);
        }

        // (4) 維持率は「40%」と規制要求のうち厳しい方（株価に依存する）。ADR-0016 決定7。
        // 株価下限を割っている銘柄は規制式の分母として意味を持たないため、(5) で既に拒否済みの場合は評価しない。
        if (intent.Price >= limits.PriceFloorUsd)
        {
            var threshold = limits.MaintenanceMarginThresholdFor(intent.Price);
            var breached = context.MaintenanceMarginRatio is { } ratio
                ? ratio < threshold
                // 維持率が取得できないとき、空売り建玉を保有していれば「割れていないこと」を確認できない。
                // 確認できないまま積み増さない（IADR-0131 決定4・フェイルクローズ）。建玉が無ければ
                // 維持率という概念自体が成立しないため対象外とする。
                : context.TotalShortExposure > 0m;
            if (breached)
            {
                reasons.Add(RejectionReason.MaintenanceMarginBreach);
            }
        }

        return reasons;
    }
}
