namespace AiStockTrading.RiskManagement.Domain;

// FR-10, UC-06, ADR-0003, ADR-0016 決定7, #330, IADR-0133: 維持率割れによる自動縮小の決定的コア（純関数）。
//
// UC-06 は本規則を「**完全に機械的である**——入力（各建玉の必要証拠金と現在の維持率）が同じであれば、
// 出力（決済する建玉と数量）は常に同じになる。利用者の承認操作も LLM 呼び出しも介在しない」と定めた。
// したがって本型は**時計も乱数も外部照会も持たない**（引数だけで出力が決まる）。
//
// 規則（05_trading-assumptions §5・2026-08-02 追補／UC-06 代替フロー）:
//   発動条件: 維持率 ≦ 適用閾値（**割り込む前に**動く。IADR-0133 決定3。「接近」の余裕 α は計画に無い＝発明しない）
//   適用閾値: 保有建玉のうち**最も厳しいもの**（IADR-0133 決定2。1 件なら MaintenanceMarginThresholdFor と一致）
//   回復目標: 適用閾値 + 5 ポイント（閾値に**連動**する。40% なら 45%、規制側が効いて 45% なら 50%）
//   縮小量:   回復目標へ届く**最小限**（一律 50% 減・建玉 1 件丸ごとは計画が明示的に棄却）
//   対象順:   **必要証拠金の降順**（回復効率が最も高い順。含み損順・建玉時刻順は棄却）
public static class MaintenanceMarginReducer
{
    /// <summary>
    /// 縮小計画を組み立てる。発動条件を満たさない（維持率が閾値を上回る・建玉が無い）場合は <c>null</c>。
    /// </summary>
    /// <param name="snapshot">維持率の実測入力（供給元は #342 / #331。null は供給なし＝発動しない）。</param>
    /// <param name="limits">空売り専用統制値。閾値・回復目標の解決は本型で再実装せず #329 のメソッドを通す。</param>
    public static MaintenanceMarginReductionPlan? Plan(MaintenanceMarginSnapshot? snapshot, ShortSellingLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        // IADR-0133 決定5: 供給が無い／建玉が無いなら決済しない。「データが無いのに決済する」は安全側ではない
        // （誤作動の害が不可逆。UC-06 の 2 統制比較表）。積み増し側の穴は #329（MaintenanceMarginBreach）が塞ぐ。
        if (snapshot is null || snapshot.Positions.Count == 0)
            return null;

        // IADR-0133 決定8: 値として成立しない建玉（株価・数量が 0 以下／必要証拠金が負）が 1 件でもあれば、
        // **スナップショット全体を信頼できない**ものとして決済しない。壊れたデータで建玉を決済する方が、
        // しないことより危険である。**壊れた建玉だけを除く案は採らない**——除くと分母（建玉評価額の合計）が
        // 縮んで維持率が実際より良く見え、過少縮小へ倒れる（統制として危険な向き）。
        // 呼び出し側が「健全で発動不要」と区別できるよう、拒否の事実は
        // MaintenanceMarginReductionService が観測可能にする（黙って何もしない状態を作らない）。
        if (!snapshot.IsTrustworthy)
            return null;

        if (snapshot.MaintenanceMarginRatio is not { } ratio)
            return null;

        // IADR-0133 決定2: 株価に依存する閾値が建玉ごとに出るため、**最も厳しいもの**を口座へ適用する
        // （FR-10「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」の延長）。
        // 閾値・回復目標の算出は ShortSellingLimits（#329・IADR-0131）だけを通す＝値の二重定義を作らない。
        var threshold = snapshot.Positions.Max(p => limits.MaintenanceMarginThresholdFor(p.PriceUsd));
        var recoveryTarget = snapshot.Positions.Max(p => limits.MaintenanceRecoveryTargetFor(p.PriceUsd));

        // IADR-0133 決定3: 閾値ちょうどで発動する（まだ割り込んでいない時点で動く＝「割り込む前に発動」）。
        if (ratio > threshold)
            return null;

        var legs = SelectLegs(snapshot, recoveryTarget);
        if (legs.Count == 0)
            return null;

        var closedValue = legs.Sum(l => l.Quantity * l.PriceUsd);
        var remainingValue = snapshot.TotalMarketValueUsd - closedValue;

        return new MaintenanceMarginReductionPlan
        {
            RatioBefore = ratio,
            Threshold = threshold,
            RecoveryTarget = recoveryTarget,
            // 全量決済した場合は維持率の概念が消える（建玉ゼロ）。
            RatioAfter = remainingValue > 0m ? snapshot.NetEquityUsd / remainingValue : null,
            Legs = legs,
        };
    }

    // 必要証拠金の降順に、必要な評価額の削減 X ＝ V − 純資産 ÷ 回復目標 を満たすまで決済する。
    // 最後の 1 件は ⌈残り ÷ 株価⌉ 株だけ決済する（建玉 1 件を丸ごと閉じる規則を計画が棄却しているため。
    // 端株が無いので切り上げが「目標に届く最小限」である）。
    private static List<MaintenanceMarginReductionLeg> SelectLegs(
        MaintenanceMarginSnapshot snapshot,
        decimal recoveryTarget)
    {
        // 回復目標が 0 以下（設定異常）・純資産が 0 以下（債務超過）なら目標へ到達できない。
        // 何もしないのは「最も危険な状態で統制が沈黙する」ことを意味するため、全量決済へ倒す（IADR-0133 決定4）。
        var requiredValueReduction = recoveryTarget > 0m && snapshot.NetEquityUsd > 0m
            ? snapshot.TotalMarketValueUsd - (snapshot.NetEquityUsd / recoveryTarget)
            : snapshot.TotalMarketValueUsd;

        var legs = new List<MaintenanceMarginReductionLeg>();
        var remaining = requiredValueReduction;

        foreach (var position in Ordered(snapshot.Positions))
        {
            if (remaining <= 0m)
                break;

            // 端株が無いため株数は切り上げ。建玉数量を超えないようにクランプする。
            // 株価が 0 以下の建玉は Plan の入口（IsTrustworthy）が**スナップショットごと**弾いているため
            // ここには来ない（ゼロ除算は起こらない）。なお閾値算出 MaintenanceMarginThresholdFor は
            // 非正の株価に対して**例外を投げる**ため、入口の検査を外すと Plan 全体が例外で中断する。
            var neededQuantity = (int)Math.Min(position.Quantity, Math.Ceiling(remaining / position.PriceUsd));

            legs.Add(new MaintenanceMarginReductionLeg
            {
                Symbol = position.Symbol,
                Market = position.Market,
                PositionSide = position.Side,
                ProductType = position.ProductType,
                Quantity = neededQuantity,
                PriceUsd = position.PriceUsd,
                // 部分決済は必要証拠金を数量で按分する（日報が決済分の必要証拠金を求めるため）。
                RequiredMarginUsd = neededQuantity == position.Quantity
                    ? position.RequiredMarginUsd
                    : position.RequiredMarginUsd * neededQuantity / position.Quantity,
            });

            remaining -= neededQuantity * position.PriceUsd;
        }

        return legs;
    }

    // UC-06: **必要証拠金の降順**。同値は評価額の降順 → 銘柄コード → 市場で決定的に解く
    // （入力の並び順に依存すると「入力が同じなら出力も同じ」が壊れる）。
    // 含み損・建玉時刻は MarginPosition が持たないため、そもそも整列に混入し得ない。
    private static IEnumerable<MarginPosition> Ordered(IReadOnlyList<MarginPosition> positions) =>
        positions
            .OrderByDescending(p => p.RequiredMarginUsd)
            .ThenByDescending(p => p.MarketValueUsd)
            .ThenBy(p => p.Symbol, StringComparer.Ordinal)
            .ThenBy(p => p.Market);
}
