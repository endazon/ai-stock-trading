using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetDriftAdoptions;

// FR-06, FR-11, FR-16, SC-03, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2:
// 取引台帳の**乖離の取り込み**を期間（取引日）で絞る純関数。報告書サービスが日報 §2-b「手動売買（損益不明）」と
// 在庫の畳み込みのため s2s 同期照会する GET /risk-controls/drift-adoptions の実体。
//
// 取引日は PortfolioProjection.TradeDate（**取り込みの市場の現地取引日**）で解釈する ——
// PeriodFillQuery と**同じ境界**である（片側だけ JST に残すと、同じ 1 日を見ているはずの §2 と §2-b がずれる）。
//
// 🔴 **約定列（GetFills / PeriodFillQuery）と混ぜない。** 取り込みは約定価格を持たず、実現損益は**不明**である。
// 混ぜると「平均取得単価で売った損益 0 の決済」が確定値として集計される（IADR-0350 決定 4）。
public static class PeriodDriftAdoptionQuery
{
    /// <summary>取引日が [fromInclusive, toInclusive] に入る取り込みを取り込み日時の昇順で返す。逆順の期間は空。</summary>
    public static IReadOnlyList<DriftAdoptionView> InTradingDayRange(
        IReadOnlyList<LedgerDriftAdoption> adoptions,
        DateOnly fromInclusive,
        DateOnly toInclusive)
    {
        ArgumentNullException.ThrowIfNull(adoptions);

        if (fromInclusive > toInclusive)
            return [];

        return [.. adoptions
            .Where(a =>
            {
                // 取り込みが台帳へ効く時刻は**取り込み日時**である（システム外の売買が実際に約定した時刻は分からない）。
                var tradingDay = PortfolioProjection.TradeDate(a.AdoptedAt, a.Market);
                return tradingDay >= fromInclusive && tradingDay <= toInclusive;
            })
            .OrderBy(a => a.AdoptedAt)
            .ThenBy(a => a.Id)
            .Select(DriftAdoptionView.Of)];
    }
}

/// <summary>
/// FR-06, FR-11, ADR-0041 決定 1, #870, IADR-0360 決定 2: 取り込み 1 件の wire 形（報告書 §2-b の供給元）。
/// <para>
/// 🔴 <b>価格を運ばない。</b> 台帳が持つ <see cref="LedgerDriftAdoption.CostBasisPrice"/> は
/// **取り込み時点の平均取得単価であって約定価格ではない**。wire へ出すと受け手が「約定単価」として扱い得るため、
/// 出さない —— 報告書の在庫の畳み込みは**その時点の自分の平均取得単価**で減らすので、そもそも要らない。
/// </para>
/// </summary>
public sealed record DriftAdoptionView(
    Guid AdoptionId,
    string Symbol,
    Market Market,
    /// <summary>台帳へ適用した減少の方向（ロングの減少は Sell・ショートの減少は Buy）。</summary>
    TradeSide Side,
    /// <summary>減らした数量（&gt; 0）。</summary>
    int Quantity,
    /// <summary>取り込み前の台帳の数量（符号付き）。</summary>
    int LedgerQuantityBefore,
    /// <summary>観測されたブローカーの数量（符号付き）＝取り込み後の台帳の数量。</summary>
    int BrokerQuantity,
    DateTimeOffset ObservedAt,
    string Actor,
    string Reason,
    DateTimeOffset AdoptedAt)
{
    /// <summary>
    /// 由来。**常に <see cref="TradeOrigin.ManualAdoption"/>** である（本エンドポイントは取り込みだけを返す）。
    /// 定数だが wire へ出す —— 受け手が「約定と同じ軸で読める」ことが由来のラベルの目的である（IADR-0360 決定 1）。
    /// </summary>
    public TradeOrigin Origin => TradeOrigin.ManualAdoption;

    /// <summary>🔴 <b>実現損益は記録していない（不明）。</b> 0 ではない（ADR-0041 決定 1 / IADR-0350 決定 3）。</summary>
    public bool RealizedPnlRecorded => false;

    public static DriftAdoptionView Of(LedgerDriftAdoption a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new DriftAdoptionView(
            a.Id, a.Symbol, a.Market, a.Side, a.Quantity,
            a.LedgerQuantityBefore, a.BrokerQuantity, a.ObservedAt, a.Actor, a.Reason, a.AdoptedAt);
    }
}
