namespace ReportService.Domain;

// FR-16, 04_report-templates 数値定義: 期間の損益集計結果。数値はコードで集計する（LLM に計算させない）。
public sealed record PnlSummary(
    /// <summary>実現損益（税引前・費用前）＝約定代金差額の合計。</summary>
    decimal RealizedPnlGross,

    /// <summary>費用合計＝売買手数料＋取引諸費用＋為替スプレッド相当（CostCalculator）。</summary>
    decimal TotalCost,

    /// <summary>源泉徴収税額＝max(0, 実現損益(税引前) − 費用合計) × 譲渡益税率（利益にのみ課税）。</summary>
    decimal TaxWithheld,

    /// <summary>実現損益（税引後・費用込み）＝実現損益(税引前) − 費用合計 − 源泉徴収税額。</summary>
    decimal RealizedPnlNet,

    /// <summary>評価損益（税引前・参考）＝Σ 建玉 (現在値 − 平均取得単価)×数量。現在値の無い建玉は 0。</summary>
    decimal UnrealizedPnl,

    /// <summary>約定件数（fills 総数）。</summary>
    int TradeCount,

    /// <summary>決済（実現が発生した）件数。</summary>
    int RealizingTradeCount,

    /// <summary>勝ち決済件数（実現損益 &gt; 0 の決済数）。勝率＝勝ち/決済（週報/月報）。</summary>
    int WinningTradeCount,

    /// <summary>
    /// FR-06, FR-16, #892, IADR-0381: <b>取得原価が当期間に無く、実現損益を算定できなかった決済の件数</b>
    /// （期間より前に建てた建玉の決済。<see cref="PeriodInventory"/>）。#1181, IADR-0493: 期間開始時点の在庫を受け取った回は、
    /// 在庫と期間の買いで賄えない手仕舞い（台帳と報告書の窓の食い違い）だけがここに数えられる。
    /// <para>
    /// 🔴 <b>0 より大きければ、この期間の実現損益（<see cref="RealizedPnlGross"/> /
    /// <see cref="RealizedPnlNet"/>）・<see cref="TaxWithheld"/>・勝率（<see cref="WinningTradeCount"/> /
    /// <see cref="RealizingTradeCount"/>）・<see cref="UnrealizedPnl"/> は部分値である。</b>
    /// レンダラ・要約は<b>数字として出さず「算定できません」と描く</b>——黙って部分値を出すことは、
    /// 幻の建玉の評価損益を出すのと同じ誤りである（#892）。
    /// </para>
    /// <para>既定 0 ＝算定できなかった決済は無い（既存の呼び出しは非破壊で通る）。</para>
    /// </summary>
    int UnvaluedSettlementCount = 0,

    /// <summary>
    /// FR-06, FR-16, #1181, IADR-0493 決定 4: <b>期間開始時点の在庫を照会できなかった</b>（供給元はあるが取得に失敗した）。
    /// <para>
    /// 🔴 <c>true</c> のとき、在庫は期間で切ったまま畳まれている。持ち越した建玉は評価損益に入らず、期間の買いで賄えた決済も
    /// 持ち越し分と混ぜた平均取得単価で算定されていない——<b>実現損益・税・勝率・評価損益は部分値である</b>
    /// （<see cref="UnvaluedSettlementCount"/> が 0 でも）。描画は <see cref="IsPartial"/> で分岐する。
    /// </para>
    /// <para>既定 <c>false</c>＝照会できた、または供給元を持たない経路（手動の生成 API・未注入の単体テスト＝従来挙動）。</para>
    /// </summary>
    bool OpeningInventoryUnknown = false)
{
    /// <summary>
    /// 取得原価を要する値（実現損益・税・勝率・評価損益）が部分値か（#892 の算定できない決済、または #1181 の在庫の照会失敗）。
    /// 🔴 <c>true</c> の値を数字として出さない。
    /// </summary>
    public bool IsPartial => UnvaluedSettlementCount > 0 || OpeningInventoryUnknown;
}
