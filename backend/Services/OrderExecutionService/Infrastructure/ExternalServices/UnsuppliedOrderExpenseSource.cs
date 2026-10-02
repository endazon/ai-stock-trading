using AiStockTrading.Shared.Contracts.Trading;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-11, ADR-0016 決定15, ADR-0027 決定4, #633, IADR-0300:
// 経費明細の供給が無い構成の**安全既定**。**常に「取得できない」を返す。**
//
// 実費の取得は moomoo の注文費用照会（Trd_GetOrderFee）に依存する。#1086 で照会そのものは実装した
// （MMApiMoomooTradeClient.QueryOrderFeeAsync。現状の呼び手は 1 回実行の検証口 `--probe-order-fee` だけ）が、
// **本ポート（IOrderExpenseSource）へは未接続**である。応答仕様（区分の粒度・通貨・手数料と諸費用の切れ目）を
// 実測で確かめ、区分への写像と重複排除（IADR-0300 決定9）を決めるまで接続しない。ここで概算（CostCalculator）を実費として
// 積むと、ADR-0027 が塞いだ「表示されている数字が何を意味するか誰も答えられない」状態へ戻る。
//
// FR-11, ADR-0027 決定4, ADR-0035 決定3, #1086, IADR-0484: **取得できない理由は発注先ごとに違う。**
// moomoo SIMULATE では Trd_GetOrderFee そのものが `retType=-1 retMsg=Paper trading is not supported.` で
// 拒否される（2026-10-02 実測）。「未接続（つなげば取れる）」と書くと、SIMULATE の間ずっと誤った理由が残る。
// 理由は合成起点で発注先（BrokerProvider）から 1 度だけ決める（<see cref="For"/>）。**推計は積まない** ——
// 計画は経費の推計を取引記録へ積むことを定めておらず、未供給は 0 とも推計とも書かない（ADR-0027 決定4）。
//
// 🔴 **空の明細（Supplied([])）を返さない。** 空を返すと「照会できて費用が 1 円も無かった」と読め、
// 供給の結線を忘れた期間がそのまま「費用なし」で通る（UnsuppliedBorrowFeeRecordSource が同じ理由で
// 空の BorrowFeeRecord ではなく null を返している）。
public sealed class UnsuppliedOrderExpenseSource : IOrderExpenseSource
{
    /// <summary>取得できない理由（診断用・固定文言）。テストがこの文言そのものを固定する。</summary>
    public const string Reason =
        "ブローカーの経費明細を本番の経費供給経路へ取り込んでいない"
        + "（moomoo の注文費用照会は実装済みで検証口から呼べるが、経費の供給ポートへは未接続）。";

    /// <summary>
    /// moomoo SIMULATE で取得できない理由（#1086・IADR-0484）。ブローカーが照会を拒否するため、
    /// 結線しても取れない。<b>推計で埋めないことも併せて述べる。</b>
    /// </summary>
    public const string MoomooSimulateReason =
        "moomoo の注文費用照会（Trd_GetOrderFee）は SIMULATE 口座では提供されない"
        + "（retMsg 'Paper trading is not supported.'・2026-10-02 実測）。"
        + "SIMULATE の間は経費の実績を取得できず、推計でも埋めない。";

    /// <summary>
    /// 内蔵 paper で取得できない理由（#1086・IADR-0484）。外部へ発注しないため、ブローカーの費用そのものが存在しない。
    /// </summary>
    public const string InternalPaperReason =
        "内蔵 paper は外部へ発注しない擬似約定であり、ブローカーの経費明細が存在しない。推計でも埋めない。";

    private readonly string _reason;

    /// <summary>既定の理由（<see cref="Reason"/>＝未接続）で作る。</summary>
    public UnsuppliedOrderExpenseSource()
        : this(Reason)
    {
    }

    private UnsuppliedOrderExpenseSource(string reason) => _reason = reason;

    /// <summary>取得できない理由。</summary>
    public string UnavailableReason => _reason;

    /// <summary>
    /// 発注先から理由を決めて作る（IADR-0484）。moomoo REAL は照会が成立し得るが未接続のため <see cref="Reason"/>。
    /// 未知の値は黙って既定へ倒さず例外にする（<see cref="BrokerSelection.ToBrokerProvider"/> と同じ流儀）。
    /// </summary>
    public static UnsuppliedOrderExpenseSource For(BrokerProvider provider) => provider switch
    {
        BrokerProvider.MoomooSimulate => new(MoomooSimulateReason),
        BrokerProvider.InternalPaper => new(InternalPaperReason),
        BrokerProvider.MoomooReal => new(Reason),
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider), provider, "未対応の発注先。経費の取得できない理由を IADR-0484 の対応表へ足してください。"),
    };

    public Task<OrderExpenseLookup> GetOrderExpensesAsync(
        OrderExpenseQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(OrderExpenseLookup.Unavailable(_reason));
}
